using System.Reflection;
using ExtremeEditor.Audio;
using NAudio.Wave;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackSpeedHitSoundRegression
{
    public static void Run()
    {
        VerifyClipShapeIsIndependentOfSpeed();
        VerifyTriggerSpacingUsesTransportSpeed();
        VerifyRuntimeSpeedSegmentsAndSingleTrigger();
        VerifyProductionRuntimeSegmentsAndSingleTrigger();
        VerifyProductionGraphContract();
    }

    private static void VerifyClipShapeIsIndependentOfSpeed()
    {
        int[] clip = [1, 2, 3, 4, 5];
        foreach (double speed in new[] { 0.01, 0.5, 1.0, 2.0, 10.0 })
        {
            ReferenceRender render = Render([new Hit(10, clip)], [new Segment(0, 0, speed)], 2_000);
            int trigger = checked((int)Math.Round(10 / speed, MidpointRounding.AwayFromZero));
            AssertSequence(clip, render.Samples.Skip(trigger).Take(clip.Length).ToArray(),
                $"hitsound clip at {speed:0.##}x");
        }
    }

    private static void VerifyTriggerSpacingUsesTransportSpeed()
    {
        int[] clip = [1];
        foreach ((double speed, int expectedSpacing) in new[]
                 {
                     (0.5, 20),
                     (2.0, 5),
                     (0.01, 1_000),
                     (10.0, 1)
                 })
        {
            ReferenceRender render = Render(
                [new Hit(10, clip), new Hit(20, clip)],
                [new Segment(0, 0, speed)],
                2_100);
            if (render.TriggerOutputFrames.Count != 2)
                throw new InvalidOperationException($"Expected two triggers at {speed:0.##}x.");
            int spacing = render.TriggerOutputFrames[1] - render.TriggerOutputFrames[0];
            if (spacing != expectedSpacing)
            {
                throw new InvalidOperationException(
                    $"Trigger spacing at {speed:0.##}x: expected={expectedSpacing}, actual={spacing}.");
            }
        }
    }

    private static void VerifyRuntimeSpeedSegmentsAndSingleTrigger()
    {
        int[] longClip = [1, 2, 3, 4, 5, 6, 7, 8];
        Hit[] hits =
        [
            new Hit(8, longClip),
            new Hit(10, [0]), // exactly on the first speed-change anchor
            new Hit(16, [30])
        ];
        Segment[] segments =
        [
            new Segment(0, 0, 1.0),
            new Segment(10, 10, 0.5),
            new Segment(12, 14, 2.0)
        ];

        ReferenceRender render = Render(hits, segments, 32);

        // The voice that began before the anchor continues one clip frame per real/output frame.
        AssertSequence(longClip, render.Samples.Skip(8).Take(longClip.Length).ToArray(),
            "active voice across runtime speed changes");

        // Anchor event belongs to exactly one segment and must never be retriggered.
        if (render.TriggerSourceFrames.Count(source => source == 10) != 1)
            throw new InvalidOperationException("A trigger on the speed-change anchor did not fire exactly once.");

        int futureTriggerIndex = render.TriggerSourceFrames.IndexOf(16);
        if (futureTriggerIndex < 0 || render.TriggerOutputFrames[futureTriggerIndex] != 16)
        {
            throw new InvalidOperationException(
                "A trigger after the speed-change anchor was not scheduled by the new rate segment.");
        }
    }

    private static void VerifyProductionGraphContract()
    {
        Type provider = typeof(AudioPlayer).Assembly.GetType("ExtremeEditor.Audio.TransportHitSoundSampleProvider")
            ?? throw new InvalidOperationException("TransportHitSoundSampleProvider is missing.");
        PropertyInfo? speed = provider.GetProperty(
            "PlaybackSpeed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? reanchor = provider.GetMethod(
            "ReanchorPlaybackSpeed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo? songRate = typeof(AudioPlayer).GetField(
            "_songRateProvider",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (speed is null || reanchor is null || songRate is null)
        {
            throw new InvalidOperationException(
                "Song-only playback-rate graph / output-domain hitsound scheduler is not available.");
        }
    }

    private static void VerifyProductionRuntimeSegmentsAndSingleTrigger()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
        var hits = new[]
        {
            ScheduledHit(sourceFrame: 8, Enumerable.Repeat(1.0f, 8).ToArray()),
            ScheduledHit(sourceFrame: 10, [100.0f]),
            ScheduledHit(sourceFrame: 16, [30.0f])
        };
        var provider = new TransportHitSoundSampleProvider(format, hits, totalSourceFrames: 32, playbackSpeed: 1.0);
        var rendered = new List<float>();

        ReadFrames(provider, rendered, 10);
        provider.ReanchorPlaybackSpeed(sourceFrame: 10, playbackSpeed: 0.5);
        ReadFrames(provider, rendered, 4);
        provider.ReanchorPlaybackSpeed(sourceFrame: 12, playbackSpeed: 2.0);
        ReadFrames(provider, rendered, 10);

        float[] left = rendered.Where((_, index) => (index & 1) == 0).ToArray();
        for (int frame = 8; frame <= 15; frame++)
        {
            float expected = frame == 10 ? 101.0f : 1.0f;
            if (left[frame] != expected)
            {
                throw new InvalidOperationException(
                    $"Production hitsound voice changed rate across anchor. frame={frame}, expected={expected}, actual={left[frame]}.");
            }
        }

        if (left.Count(sample => sample >= 100.0f) != 1)
            throw new InvalidOperationException("Production trigger on the speed-change anchor did not fire exactly once.");
        if (left[16] != 30.0f)
        {
            throw new InvalidOperationException(
                $"Production trigger after anchor did not use the new rate segment. expected=30, actual={left[16]}.");
        }
    }

    private static SampleAccurateHitSoundProvider.ScheduledHit ScheduledHit(long sourceFrame, float[] mono)
    {
        var stereo = new float[mono.Length * 2];
        for (int frame = 0; frame < mono.Length; frame++)
        {
            stereo[frame * 2] = mono[frame];
            stereo[frame * 2 + 1] = mono[frame];
        }
        return new SampleAccurateHitSoundProvider.ScheduledHit(
            new RenderedHitSound(stereo, OffsetSeconds: 0),
            sourceFrame,
            Volume: 1.0f);
    }

    private static void ReadFrames(
        TransportHitSoundSampleProvider provider,
        List<float> destination,
        int frameCount)
    {
        var buffer = new float[frameCount * provider.WaveFormat.Channels];
        int read = provider.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
            throw new InvalidOperationException($"Production hitsound provider ended early. expected={buffer.Length}, actual={read}.");
        destination.AddRange(buffer);
    }

    private static ReferenceRender Render(Hit[] hits, Segment[] segments, int outputFrames)
    {
        var samples = new int[outputFrames];
        var triggerOutputFrames = new List<int>();
        var triggerSourceFrames = new List<int>();
        var voices = new List<Voice>();

        for (int output = 0; output < outputFrames; output++)
        {
            foreach (Hit hit in hits)
            {
                int triggerOutput = SourceToOutput(hit.SourceFrame, segments);
                if (triggerOutput != output)
                    continue;

                triggerOutputFrames.Add(output);
                triggerSourceFrames.Add(hit.SourceFrame);
                voices.Add(new Voice(hit.Clip, output));
            }

            foreach (Voice voice in voices)
            {
                int clipFrame = output - voice.StartOutputFrame;
                if ((uint)clipFrame < (uint)voice.Clip.Length)
                    samples[output] += voice.Clip[clipFrame];
            }
        }

        return new ReferenceRender(samples, triggerOutputFrames, triggerSourceFrames);
    }

    private static int SourceToOutput(int sourceFrame, Segment[] segments)
    {
        Segment selected = segments[0];
        foreach (Segment segment in segments)
        {
            if (segment.SourceAnchor > sourceFrame)
                break;
            selected = segment;
        }

        return checked((int)Math.Round(
            selected.OutputAnchor + (sourceFrame - selected.SourceAnchor) / selected.Speed,
            MidpointRounding.AwayFromZero));
    }

    private static void AssertSequence(int[] expected, int[] actual, string label)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected=[{string.Join(',', expected)}], actual=[{string.Join(',', actual)}].");
        }
    }

    private sealed record Voice(int[] Clip, int StartOutputFrame);
    private readonly record struct Hit(int SourceFrame, int[] Clip);
    private readonly record struct Segment(int SourceAnchor, int OutputAnchor, double Speed);
    private readonly record struct ReferenceRender(
        int[] Samples,
        List<int> TriggerOutputFrames,
        List<int> TriggerSourceFrames);
}
