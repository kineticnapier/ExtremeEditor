using System.Reflection;
using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class DenseTransportBlockMixRegression
{
    internal static void Run()
    {
        // RED: the transport mixer must perform voice overlap-add per Read block
        // rather than repeatedly scanning all active voices for every sample.
        MethodInfo? method = typeof(TransportHitSoundSampleProvider).GetMethod(
            "MixActiveVoicesIntoBlock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
            throw new InvalidOperationException(
                "RED: TransportHitSoundSampleProvider must mix active voices once per output block, not scan them every sample frame.");

        CheckDenseOverlaps(0.75);
        CheckDenseOverlaps(2.0);
        CheckSpeedChangePreservesExactTriggers();
    }

    private static void CheckDenseOverlaps(double speed)
    {
        const int clipFrames = 73;
        const int count = 1_200;
        const int totalOutputFrames = 3_000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        float[] clipPcm = Enumerable.Range(0, clipFrames)
            .SelectMany(frame => new[] { 0.001f * (frame + 1), 0.001f * (frame + 1) })
            .ToArray();
        var clip = new RenderedHitSound(clipPcm, 0.0);
        var hits = Enumerable.Range(0, count)
            .Select(i => new SampleAccurateHitSoundProvider.ScheduledHit(
                clip, StartFrame: 100 + i / 3, Volume: 0.125f))
            .ToArray();
        var provider = new TransportHitSoundSampleProvider(
            format, hits, totalSourceFrames: 10_000, playbackSpeed: speed);

        float[] actual = new float[totalOutputFrames * 2];
        int written = 0;
        foreach (int requested in new[] { 71, 137, 1024, 333, 777, 658 })
        {
            var buffer = new float[requested * 2];
            int n = provider.Read(buffer, 0, buffer.Length);
            if (n != buffer.Length)
                throw new InvalidOperationException(
                    $"dense transport at {speed}x ended early: expected {buffer.Length}, actual {n}");
            Array.Copy(buffer, 0, actual, written * 2, n);
            written += requested;
        }

        if (written != totalOutputFrames)
            throw new InvalidOperationException("Dense transport test blocks do not cover the expected duration.");

        var expected = new float[totalOutputFrames * 2];
        foreach (var hit in hits)
        {
            long start = (long)Math.Round(hit.StartFrame / speed, MidpointRounding.AwayFromZero);
            for (int i = 0; i < clipFrames; i++)
            {
                long dest = start + i;
                if (dest >= totalOutputFrames)
                    break;
                expected[dest * 2] += clipPcm[i * 2] * hit.Volume;
                expected[dest * 2 + 1] += clipPcm[i * 2 + 1] * hit.Volume;
            }
        }

        for (int frame = 0; frame < totalOutputFrames; frame++)
        {
            int index = frame * 2;
            if (Math.Abs(actual[index] - expected[index]) > 0.00025f ||
                Math.Abs(actual[index + 1] - expected[index + 1]) > 0.00025f)
                throw new InvalidOperationException(
                    $"dense transport at {speed}x differs at frame {frame}: expected={expected[index]}, actual={actual[index]}.");
        }
    }

    private static void CheckSpeedChangePreservesExactTriggers()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var clip = new RenderedHitSound(
            Enumerable.Repeat(0.25f, 32).ToArray(), 0.0);
        var hits = new[]
        {
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, 6, 1f),
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, 10, 1f),
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, 16, 1f)
        };
        var provider = new TransportHitSoundSampleProvider(
            format, hits, totalSourceFrames: 100, playbackSpeed: 1.0);
        float[] first = new float[20];
        int read = provider.Read(first, 0, first.Length);
        if (read != 20)
            throw new InvalidOperationException("Speed-change setup read ended early.");
        provider.ReanchorPlaybackSpeed(10, 0.5);
        float[] second = new float[30];
        read = provider.Read(second, 0, second.Length);
        if (read != 30)
            throw new InvalidOperationException("Speed-change continuation ended early.");
        // The new source-frame 16 trigger lands 12 output frames after
        // the 10-frame anchor; existing voices retain their device-rate length.
        if (second[24] < 0.24f)
            throw new InvalidOperationException("A future trigger was dropped after speed reanchoring.");
    }
}
