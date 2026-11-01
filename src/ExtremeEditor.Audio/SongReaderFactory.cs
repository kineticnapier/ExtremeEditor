using NAudio.Vorbis;
using NAudio.Wave;
namespace ExtremeEditor.Audio;
internal static class SongReaderFactory
{
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
            reader.Dispose();
        }
        return new AudioFileReader(path);
    }
}
