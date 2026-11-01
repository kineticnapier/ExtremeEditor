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
            if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                return reader;

            if (reader.WaveFormat is WaveFormatExtensible extended &&
                extended.SubFormat == PcmSubtype &&
                extended.BitsPerSample is 16 or 24 or 32)
            {
                // WAVE_FORMAT_EXTENSIBLE carries ordinary PCM bytes. Expose
                // the corresponding PCM sample format without calling ACM.
                return new PcmExtensibleWaveStream(reader, extended);
            }

            // Never silently fall back to ACM for an unsupported WAV subtype.
            // Report the actual format so we can diagnose a specific file.
            string detail = reader.WaveFormat is WaveFormatExtensible other
                ? $" subtype={other.SubFormat}"
                : string.Empty;
            string format = reader.WaveFormat.Encoding.ToString();
            string type = reader.WaveFormat.GetType().Name;
            reader.Dispose();
            throw new NotSupportedException(
                $"WAV decoding unsupported: encoding={format} type={type}{detail}");
        }

        return new AudioFileReader(path);
    }

    private sealed class PcmExtensibleWaveStream : WaveStream
    {
        private readonly WaveFileReader _source;
        private readonly WaveFormat _format;

        internal PcmExtensibleWaveStream(WaveFileReader source, WaveFormatExtensible format)
        {
            _source = source;
            _format = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
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
