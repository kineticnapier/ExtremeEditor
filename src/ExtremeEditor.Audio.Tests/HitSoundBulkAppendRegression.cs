using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class HitSoundBulkAppendRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckReadAll([], 1, 1);
        CheckReadAll(CreateSamples(103), 1, 1);
        CheckReadAll(CreateSamples(4_099), 3, 1);
        CheckReadAll(CreateSamples(22_073), 22_050, 2);
    }

    private static float[] CreateSamples(int length) =>
        Enumerable.Range(0, length).Select(i => (i % 257 - 128) / 256f).ToArray();

    private static void CheckReadAll(float[] expected, int maxRead, int initialCapacity)
    {
        var provider = new FragmentedSampleProvider(expected, maxRead);
        float[] actual = HitSoundLibrary.ReadAll(
            provider, initialCapacity, "bulk-append-regression", null);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Hit-sound bulk append changed PCM for {expected.Length} samples " +
                $"with max read {maxRead} and initial capacity {initialCapacity}.");
        if (provider.ReadCalls == 0)
            throw new InvalidOperationException("Hit-sound bulk append did not read the source.");
    }

    private sealed class FragmentedSampleProvider(float[] samples, int maxRead) : ISampleProvider
    {
        private int _position;
        public int ReadCalls { get; private set; }
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            ReadCalls++;
            int length = Math.Min(Math.Min(count, maxRead), samples.Length - _position);
            if (length <= 0)
                return 0;
            Array.Copy(samples, _position, buffer, offset, length);
            _position += length;
            return length;
        }
    }
}
