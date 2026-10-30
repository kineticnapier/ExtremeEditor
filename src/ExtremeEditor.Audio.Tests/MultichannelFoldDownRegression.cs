using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class MultichannelFoldDownRegression
{
    internal static void Run()
    {
        const int sampleRate = 48_000;

        // Preserve existing stereo inputs without wrapping or modifying them.
        var stereo = new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            [0.25f, -0.5f]);
        if (!ReferenceEquals(stereo, AudioGraphFactory.NormalizeSong(stereo)))
            throw new InvalidOperationException("Stereo input should remain unchanged.");

        // A third-channel-only frame must remain audible after conversion.
        var three = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0f, 0f, 1f, 0.25f, -0.25f, 0f]));
        if (three.WaveFormat.Channels != 2 || three.WaveFormat.SampleRate != sampleRate)
            throw new InvalidOperationException("3ch format was not converted to 48 kHz stereo.");

        float[] threeOut = new float[6];
        int read = three.Read(threeOut, 1, 4);
        if (read != 4 || threeOut[0] != 0f || threeOut[5] != 0f)
            throw new InvalidOperationException("3ch output count/offset contract failed.");
        Near(0.70710678f, threeOut[1], "third channel contributes to left");
        Near(0.70710678f, threeOut[2], "third channel contributes to right");
        Near(0.25f, threeOut[3], "front left remains unchanged");
        Near(-0.25f, threeOut[4], "front right remains unchanged");
        if (three.Read(threeOut, 0, 4) != 0)
            throw new InvalidOperationException("3ch source must reach end-of-stream.");

        // A signal carried only in the sixth channel must not be discarded.
        var six = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 6),
            [0f, 0f, 0f, 0f, 0f, 1f]));
        float[] sixOut = new float[2];
        if (six.Read(sixOut, 0, 2) != 2)
            throw new InvalidOperationException("6ch frame count changed.");
        Near(0.70710678f / 4f, sixOut[0], "sixth channel contributes to left");
        Near(0.70710678f / 4f, sixOut[1], "sixth channel contributes to right");

        // Existing mono duplication must remain intact.
        var mono = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1), [0.4f]));
        float[] monoOut = new float[2];
        if (mono.Read(monoOut, 0, 2) != 2)
            throw new InvalidOperationException("Mono frame count changed.");
        Near(0.4f, monoOut[0], "mono left");
        Near(0.4f, monoOut[1], "mono right");
    }

    private static void Near(float expected, float actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.00001f)
            throw new InvalidOperationException(
                $"{name}: expected {expected}, actual {actual}");
    }

    private sealed class FixedSamples : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        internal FixedSamples(WaveFormat waveFormat, float[] samples)
        {
            WaveFormat = waveFormat;
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
