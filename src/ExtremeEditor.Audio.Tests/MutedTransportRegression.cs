using System.Reflection;
using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class MutedTransportRegression
{
    internal static void Run()
    {
        CheckMutedClockAndResume();
        CheckPostSongTail();
        CheckSpeedChangeWhileMuted();
        CheckSeekWhileMuted();
    }

    private static void CheckMutedClockAndResume()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var transport = CreateTransport(format, 24, 1.0, 2, 12, 18);
        var song = new AudioGraphFactory.FiniteSilentSampleProvider(format)
        {
            TotalFrames = 24
        };
        var graph = new UnifiedAudioSampleProvider(song, transport)
        {
            HitSoundsEnabled = false
        };

        float[] muted = ReadExact(graph, 10);
        AllNear(0, muted, "initial muted block");
        if (graph.GetRealtimeDiagnostics().HitSoundReadTicks != 0)
            throw new InvalidOperationException("Muted transport must not render hit PCM.");
        float[] mixBuffer = (float[])typeof(UnifiedAudioSampleProvider)
            .GetField("_hitBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(graph)!;
        if (mixBuffer.Length != 0)
            throw new InvalidOperationException("Muted transport must not allocate a hit mix buffer.");

        graph.HitSoundsEnabled = true;
        float[] resumed = ReadExact(graph, 4);
        Near(0, resumed[0], "no replay of hit at frame 2");
        Near(0, resumed[2], "silence before hit at frame 12");
        Near(0.25f, resumed[4], "hit at frame 12");
        Near(0.25f, resumed[6], "tail at frame 13");

        graph.HitSoundsEnabled = false;
        AllNear(0, ReadExact(graph, 5), "second muted block");
        graph.HitSoundsEnabled = true;
        float[] tail = ReadExact(graph, 3);
        Near(0.25f, tail[0], "resumed hit tail at frame 19");
        Near(0.25f, tail[2], "resumed hit tail at frame 20");
        Near(0.25f, tail[4], "resumed hit tail at frame 21");
    }

    private static void CheckPostSongTail()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var transport = CreateTransport(format, 8, 1.0, 7);
        var song = new AudioGraphFactory.FiniteSilentSampleProvider(format)
        {
            TotalFrames = 8
        };
        var graph = new UnifiedAudioSampleProvider(song, transport)
        {
            HitSoundsEnabled = false
        };

        AllNear(0, ReadExact(graph, 9), "muted transport after song EOF");
        graph.HitSoundsEnabled = true;
        float[] tail = ReadExact(graph, 2);
        Near(0.25f, tail[0], "post-song tail frame 9");
        Near(0.25f, tail[2], "post-song tail frame 10");
        if (graph.Read(new float[8], 0, 8) != 0)
            throw new InvalidOperationException("Post-song hit tail must terminate at its original end.");
    }

    private static void CheckSpeedChangeWhileMuted()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var transport = CreateTransport(format, 40, 1.0, 12);
        if (transport.AdvanceMuted(10) != 10)
            throw new InvalidOperationException("Muted transport stopped early before speed change.");
        transport.ReanchorPlaybackSpeed(10, 0.5);
        if (transport.AdvanceMuted(2) != 2)
            throw new InvalidOperationException("Muted transport stopped early after speed change.");
        transport.ResumeAfterMute();
        var buffer = new float[8];
        if (transport.Read(buffer, 0, buffer.Length) != buffer.Length)
            throw new InvalidOperationException("Resumed transport stopped early.");
        Near(0, buffer[0], "speed change silence at frame 12");
        Near(0, buffer[2], "speed change silence at frame 13");
        Near(0.25f, buffer[4], "speed change trigger at frame 14");
    }

    private static void CheckSeekWhileMuted()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var transport = CreateTransport(format, 30, 1.0, 12);
        var song = new AudioGraphFactory.FiniteSilentSampleProvider(format)
        {
            TotalFrames = 30
        };
        var graph = new UnifiedAudioSampleProvider(song, transport)
        {
            HitSoundsEnabled = false
        };
        graph.Seek(10);
        AllNear(0, ReadExact(graph, 1), "seek then mute");
        graph.HitSoundsEnabled = true;
        float[] resumed = ReadExact(graph, 2);
        Near(0, resumed[0], "seek before trigger");
        Near(0.25f, resumed[2], "seek preserves next trigger");
    }

    private static TransportHitSoundSampleProvider CreateTransport(
        WaveFormat format, long totalSourceFrames, double speed, params long[] starts)
    {
        var clip = new RenderedHitSound(
            Enumerable.Repeat(0.25f, 8).ToArray(), 0.0);
        var hits = starts.Select(start =>
            new SampleAccurateHitSoundProvider.ScheduledHit(clip, start, 1f)).ToArray();
        return new TransportHitSoundSampleProvider(format, hits, totalSourceFrames, speed);
    }

    private static float[] ReadExact(UnifiedAudioSampleProvider graph, int frames)
    {
        float[] buffer = new float[frames * 2];
        int read = graph.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
            throw new InvalidOperationException(
                $"Expected {buffer.Length} samples, got {read}.");
        return buffer;
    }

    private static void AllNear(float expected, float[] values, string label)
    {
        for (int i = 0; i < values.Length; i++)
            Near(expected, values[i], $"{label}, sample {i}");
    }

    private static void Near(float expected, float actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.00001f)
            throw new InvalidOperationException(
                $"{label}: expected {expected}, actual {actual}");
    }
}
