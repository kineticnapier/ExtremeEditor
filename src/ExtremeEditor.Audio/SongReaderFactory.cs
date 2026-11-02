using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

internal static class SongReaderFactory
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    internal static WaveStream Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string ext = Path.GetExtension(path);
        if (string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase))
            return new VorbisWaveReader(path);

        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new WaveFileReader(path);
            try
            {
                if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                    return reader;

                if (reader.WaveFormat.Encoding == WaveFormatEncoding.Extensible &&
                    reader.WaveFormat.BitsPerSample is 16 or 24 or 32 &&
                    TryGetPcmSubtype(path))
                {
                    // WAVE_FORMAT_EXTENSIBLE carries ordinary PCM bytes. Expose
                    // the corresponding PCM sample format without calling ACM.
                    return new PcmExtensibleWaveStream(reader);
                }

                // Never silently fall back to ACM for an unsupported WAV subtype.
                // Report the actual format so we can diagnose a specific file.
                string detail = reader.WaveFormat is WaveFormatExtensible other
                    ? $" subtype={other.SubFormat}"
                    : string.Empty;
                string format = reader.WaveFormat.Encoding.ToString();
                string type = reader.WaveFormat.GetType().Name;
                throw new NotSupportedException(
                    $"WAV decoding unsupported: encoding={format} type={type}{detail}");
            }
            catch
            {
                // Includes failures while inspecting the extensible header.
                // Returning either reader above transfers ownership to the caller.
                reader.Dispose();
                throw;
            }
        }

        return new AudioFileReader(path);
    }

    internal static bool TryGetPcmSubtype(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46464952) return false; // RIFF
        uint riffSize = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) return false; // WAVE
        // RIFF size excludes its initial 8 bytes. Do not accept fmt chunks
        // outside the declared RIFF container, even if the file has trailing data.
        long riffEnd = 8L + riffSize;
        if (riffSize < 4 || riffEnd > stream.Length) return false;
        while (stream.Position + 8 <= riffEnd)
        {
            uint chunk = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long next = stream.Position + length + (length & 1);
            if (next > riffEnd) return false;
            if (chunk == 0x20746D66) // fmt 
            {
                if (length < 40) return false;
                ushort encoding = reader.ReadUInt16();
                stream.Position += 12; // channels, sample rate, byte rate, block align
                ushort containerBits = reader.ReadUInt16();
                ushort cbSize = reader.ReadUInt16();
                // The declared extension must fit inside the fmt chunk (16-byte base + 2-byte cbSize).
                // A larger cbSize would claim bytes belonging to the next chunk.
                if (encoding != 0xFFFE || cbSize < 22 || cbSize > length - 18) return false;
                ushort validBits = reader.ReadUInt16();
                reader.ReadUInt32(); // channel mask
                Guid subtype = new(reader.ReadBytes(16));
                // Valid bits cannot exceed the storage width. Otherwise a
                // malformed extensible header would be treated as ordinary PCM.
                return subtype == PcmSubtype && validBits > 0 &&
                       validBits <= containerBits && containerBits is 16 or 24 or 32;
            }
            stream.Position = next;
        }
        return false;
    }

    private sealed class PcmExtensibleWaveStream : WaveStream
    {
        private readonly WaveFileReader _source;
        private readonly WaveFormat _format;

        internal PcmExtensibleWaveStream(WaveFileReader source)
        {
            _source = source;
            _format = new WaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.BitsPerSample, source.WaveFormat.Channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _source.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
