using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class LiveAudioPitchSeekRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string songPath = Path.Combine(
            Path.GetTempPath(), $"extreme-editor-live-pitch-seek-{Guid.NewGuid():N}.wav");
        try
        {
            const int sampleRate = 48_000;
            const int sourceFrames = 2_048;
            float[] source = new float[sourceFrames * 2];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                source[frame * 2] = frame / 2048.0f;
                source[frame * 2 + 1] = frame / 4096.0f;
            }

            using (var writer = new WaveFileWriter(
                songPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
                writer.WriteSamples(source, 0, source.Length);

            Type playerType = typeof(AudioPlayer);
            FieldInfo readerField = playerType.GetField(
                "_reader", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Audio reader field is missing.");
            FieldInfo pitchField = playerType.GetField(
                "_songPitch", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Song pitch field is missing.");
            FieldInfo formatField = playerType.GetField(
                "_outputFormat", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Output format field is missing.");
            MethodInfo seek = playerType.GetMethod(
                "SeekReaderToTransportTime", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Live song seek helper is missing.");

            foreach (double pitch in new[] { 75.0, 100.0, 115.0, 133.0 })
            {
                foreach (int startFrame in new[] { 24, 48, 51, 82 })
                {
                    using var player = new AudioPlayer(loadHitSoundAssets: false);
                    var reader = new WaveFileReader(songPath);
                    readerField.SetValue(player, reader);
                    pitchField.SetValue(player, pitch / 100.0);
                    formatField.SetValue(player, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));

                    object? result = seek.Invoke(player, [startFrame / (double)sampleRate]);
                    if (result is not double phase)
                        throw new InvalidOperationException(
                            "Live song seek must return the fractional source frame.");

                    double sourcePosition = startFrame * pitch / 100.0;
                    long integralFrame = (long)Math.Floor(sourcePosition);
                    double expectedPhase = sourcePosition - integralFrame;
                    if (reader.Position != integralFrame * reader.WaveFormat.BlockAlign ||
                        Math.Abs(phase - expectedPhase) > 1e-9)
                    {
                        throw new InvalidOperationException(
                            $"Pitch {pitch}%, frame {startFrame}: incorrect source seek/phase.");
                    }

                    var rate = new VariableRateSampleProvider(
                        reader.ToSampleProvider(), pitch / 100.0);
                    rate.SetInitialPhase(phase);
                    float[] actual = new float[2];
                    if (rate.Read(actual, 0, actual.Length) != actual.Length)
                        throw new InvalidOperationException("Live song seek returned too few samples.");

                    float expectedLeft = (float)(sourcePosition / 2048.0);
                    float expectedRight = (float)(sourcePosition / 4096.0);
                    if (Math.Abs(actual[0] - expectedLeft) > 0.000001f ||
                        Math.Abs(actual[1] - expectedRight) > 0.000001f)
                    {
                        throw new InvalidOperationException(
                            $"Pitch {pitch}%, frame {startFrame}: " +
                            $"expected ({expectedLeft}, {expectedRight}), " +
                            $"actual ({actual[0]}, {actual[1]}).");
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(songPath))
                File.Delete(songPath);
        }
    }
}
