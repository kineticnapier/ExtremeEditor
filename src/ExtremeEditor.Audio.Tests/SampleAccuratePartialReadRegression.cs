using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class SampleAccuratePartialReadRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckPartialReads();
        CheckSeekDropsPendingChannel();
    }

    private static SampleAccurateHitSoundProvider Create()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        level.SeparateCountdownTime = false;
        level.CountdownTicks = 0;
        var timing = new TimingMap([
            new FloorTiming(0, 0, 0, 0, 0, 0, 0, false, false),
            new FloorTiming(1, 0.001, 0.001, 0, 0, 0, 0, false, false)
        ]);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.125f, 0.375f, 0.5f, 0.25f], 0.0)
        };
        return new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2),
            level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames: 8);
    }

    private static void CheckPartialReads()
    {
        var aligned = Create();
        var expected = new float[16];
        if (aligned.Read(expected, 0, expected.Length) != expected.Length ||
            expected.All(sample => sample == 0f))
            throw new InvalidOperationException("Hit-sound partial-read fixture must contain audible PCM.");

        var partial = Create();
        int[] requests = [1, 3, 1, 5, 2];
        int position = 0;
        int index = 0;
        while (position < expected.Length)
        {
            int requested = Math.Min(requests[index++ % requests.Length], expected.Length - position);
            float[] destination = Enumerable.Repeat(-17f, requested + 4).ToArray();
            int read = partial.Read(destination, 2, requested);
            if (read != requested)
                throw new InvalidOperationException(
                    $"Hit-sound partial read returned {read} instead of {requested}.");
            for (int i = 0; i < read; i++)
            {
                if (destination[i + 2] != expected[position + i])
                    throw new InvalidOperationException(
                        $"Hit-sound PCM changed at sample {position + i}.");
            }
            if (destination[0] != -17f || destination[1] != -17f ||
                destination[^1] != -17f || destination[^2] != -17f)
                throw new InvalidOperationException("Hit-sound partial read overwrote guard samples.");
            position += read;
        }
        if (partial.PositionFrames != 8)
            throw new InvalidOperationException("Hit-sound partial reads advanced the clock incorrectly.");
    }

    private static void CheckSeekDropsPendingChannel()
    {
        var provider = Create();
        if (provider.Read(new float[1], 0, 1) != 1)
            throw new InvalidOperationException("Hit-sound one-sample read must not return zero.");
        provider.Seek(0);
        var actual = new float[4];
        var expected = new float[4];
        if (provider.Read(actual, 0, 4) != 4 ||
            Create().Read(expected, 0, 4) != 4 ||
            !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Hit-sound Seek retained an old pending channel.");
    }
}
