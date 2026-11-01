using System.IO;
using ExtremeEditor.Audio;
using NAudio.Wave;
internal static class PcmWaveReaderRegression
{
    internal static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ee-pcm24-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8.ToArray());
                writer.Write(48);
                writer.Write("WAVE"u8.ToArray());
                writer.Write("fmt "u8.ToArray());
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)2);
                writer.Write(48000);
                writer.Write(288000);
                writer.Write((short)6);
                writer.Write((short)24);
                writer.Write("data"u8.ToArray());
                writer.Write(12);
                writer.Write(new byte[] { 0,0,0, 0,0,0, 0,0,64, 0,0,192 });
            }
            using WaveStream reader = SongReaderFactory.Open(path);
            if (reader is not WaveFileReader || reader.WaveFormat.BitsPerSample != 24)
                throw new InvalidOperationException("PCM24 WAV must use direct WaveFileReader.");
            float[] samples = new float[4];
            int count = reader.ToSampleProvider().Read(samples, 0, 4);
            if (count != 4 || Math.Abs(samples[2] - 0.5f) > 0.00001f ||
                Math.Abs(samples[3] + 0.5f) > 0.00001f)
                throw new InvalidOperationException("PCM24 sample conversion failed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
