using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class TransportPartialReadRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckPartialReads(1.0);
        CheckPartialReads(2.0);
        CheckSeekDropsPendingFrame();
        CheckSpeedReanchorDropsPendingFrame();
        CheckMuteDropsPendingFrame();
    }

    private static TransportHitSoundSampleProvider Create(double speed)
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var clip = new RenderedHitSound(
            [0.25f, 0.5f, 0.5f, 0.25f, 0.75f, 0.125f], 0.0);
        var hits = new[]
        {
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, 1, 1f),
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, 4, 0.5f)
        };
        return new TransportHitSoundSampleProvider(format, hits, 6, speed);
    }

    private static void CheckPartialReads(double speed)
    {
        var aligned = Create(speed);
        var expectedBuffer = new float[128];
        int total = aligned.Read(expectedBuffer, 0, expectedBuffer.Length);
        if (total <= 0 || aligned.Read(new float[2], 0, 2) != 0)
            throw new InvalidOperationException("Aligned transport fixture did not reach EOF.");

        var partial = Create(speed);
        int[] requests = [1, 3, 1, 5, 2];
        int position = 0;
        int requestIndex = 0;
        while (position < total)
        {
            int requested = requests[requestIndex++ % requests.Length];
            var destination = Enumerable.Repeat(-17f, requested + 4).ToArray();
            int read = partial.Read(destination, 2, requested);
            int expectedRead = Math.Min(requested, total - position);
            if (read != expectedRead)
                throw new InvalidOperationException(
                    $"Transport {speed}x short read at sample {position}: expected {expectedRead}, got {read}.");
            for (int i = 0; i < read; i++)
            {
                if (Math.Abs(destination[2 + i] - expectedBuffer[position + i]) > 0.00001f)
                    throw new InvalidOperationException(
                        $"Transport {speed}x partial PCM mismatch at sample {position + i}.");
            }
            if (destination[0] != -17f || destination[1] != -17f ||
                destination[^1] != -17f || destination[^2] != -17f)
                throw new InvalidOperationException("Transport partial read wrote outside its destination range.");
            position += read;
        }

        if (partial.Read(new float[1], 0, 1) != 0)
            throw new InvalidOperationException("Transport partial read must return EOF after the final sample.");
    }

    private static void CheckSeekDropsPendingFrame()
    {
        var provider = Create(1.0);
        if (provider.Read(new float[1], 0, 1) != 1)
            throw new InvalidOperationException("Transport setup partial read failed.");
        provider.Seek(0);
        var actual = new float[4];
        int read = provider.Read(actual, 0, actual.Length);
        var expected = new float[4];
        Create(1.0).Read(expected, 0, expected.Length);
        if (read != 4 || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Transport Seek replayed a stale pending channel.");
    }

    private static void CheckSpeedReanchorDropsPendingFrame()
    {
        var provider = Create(1.0);
        if (provider.Read(new float[1], 0, 1) != 1)
            throw new InvalidOperationException("Transport reanchor setup failed.");
        provider.ReanchorPlaybackSpeed(0, 2.0);
        var actual = new float[4];
        int read = provider.Read(actual, 0, actual.Length);
        var expected = new float[4];
        Create(2.0).Read(expected, 0, expected.Length);
        if (read != 4 || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Transport speed reanchor retained a stale pending channel.");
    }

    private static void CheckMuteDropsPendingFrame()
    {
        var provider = Create(1.0);
        if (provider.Read(new float[1], 0, 1) != 1 ||
            provider.AdvanceMuted(1) != 1)
            throw new InvalidOperationException("Transport mute setup failed.");
        provider.ResumeAfterMute();
        var actual = new float[4];
        int read = provider.Read(actual, 0, actual.Length);
        var expected = new float[4];
        var reference = Create(1.0);
        reference.Seek(2);
        reference.Read(expected, 0, expected.Length);
        if (read != 4 || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Transport mute resume retained a stale pending channel.");
    }
}
