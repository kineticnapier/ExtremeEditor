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
            string extensiblePath = Path.Combine(Path.GetTempPath(), $"ee-extensible24-{Guid.NewGuid():N}.wav");
            try
            {
                using (var writer = new BinaryWriter(File.Create(extensiblePath)))
                {
                    writer.Write("RIFF"u8.ToArray());
                    writer.Write(72);
                    writer.Write("WAVE"u8.ToArray());
                    writer.Write("fmt "u8.ToArray());
                    writer.Write(40);
                    writer.Write((ushort)0xFFFE);
                    writer.Write((short)2);
                    writer.Write(48000);
                    writer.Write(288000);
                    writer.Write((short)6);
                    writer.Write((short)24);
                    writer.Write((short)22);
                    writer.Write((short)24);
                    writer.Write(3);
                    writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
                    writer.Write("data"u8.ToArray());
                    writer.Write(12);
                    writer.Write(new byte[] { 0,0,0, 0,0,0, 0,0,64, 0,0,192 });
                }
                if (!SongReaderFactory.TryGetPcmSubtype(extensiblePath))
                    throw new InvalidOperationException("Valid extensible PCM24 header was rejected.");
                using WaveStream extended = SongReaderFactory.Open(extensiblePath);
                if (extended.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                    throw new InvalidOperationException("Extensible PCM24 was not normalized to direct PCM.");
                float[] data = new float[4];
                int read = extended.ToSampleProvider().Read(data, 0, data.Length);
                if (read != 4 || Math.Abs(data[2] - 0.5f) > 0.00001f ||
                    Math.Abs(data[3] + 0.5f) > 0.00001f)
                    throw new InvalidOperationException("Extensible PCM24 sample values changed.");
            }
            finally { if (File.Exists(extensiblePath)) File.Delete(extensiblePath); }
            VerifyMalformedExtensibleHeader();
            VerifyTruncatedRiffHeader();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void VerifyTruncatedRiffHeader()
    {
        // All prefixes of a 12-byte RIFF/WAVE header are malformed.
        // The format probe should reject them, not throw EndOfStreamException.
        byte[] header =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            72, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E'
        ];
        string path = Path.Combine(Path.GetTempPath(), $"ee-short-riff-{Guid.NewGuid():N}.wav");
        try
        {
            for (int length = 0; length < header.Length; length++)
            {
                File.WriteAllBytes(path, header.AsSpan(0, length).ToArray());
                if (SongReaderFactory.TryGetPcmSubtype(path))
                    throw new InvalidOperationException(
                        $"A truncated RIFF header of {length} bytes was accepted.");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void VerifyMalformedExtensibleHeader()
    {
        VerifyMalformedExtensibleHeader(32, 22,
            "Extensible PCM must reject valid bits larger than its container width.");
        VerifyMalformedExtensibleHeader(24, 23,
            "Extensible PCM must reject an extension length beyond its fmt chunk.");
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a fmt chunk outside the declared RIFF container.", riffSize: 4);
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a RIFF container truncated by the file end.", riffSize: 76);
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a block alignment inconsistent with its channels and bit depth.",
            blockAlign: 4);
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a byte rate inconsistent with its sample rate and block alignment.",
            byteRate: 12345);
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a zero sample rate.", sampleRate: 0);
        VerifyMalformedExtensibleHeader(24, 22,
            "Extensible PCM must reject a zero channel count.", channels: 0);
    }

    private static void VerifyMalformedExtensibleHeader(
        short validBits, short extensionSize, string message, int riffSize = 72,
        short channels = 2, int sampleRate = 48000, int byteRate = 288000,
        short blockAlign = 6)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ee-invalid-extensible-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8.ToArray());
                writer.Write(riffSize);
                writer.Write("WAVE"u8.ToArray());
                writer.Write("fmt "u8.ToArray());
                writer.Write(40);
                writer.Write((ushort)0xFFFE);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write((short)24); // container width
                writer.Write(extensionSize);
                writer.Write(validBits);
                writer.Write(3);
                writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
                writer.Write("data"u8.ToArray());
                writer.Write(12);
                writer.Write(new byte[12]);
            }

            if (SongReaderFactory.TryGetPcmSubtype(path))
                throw new InvalidOperationException(message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
