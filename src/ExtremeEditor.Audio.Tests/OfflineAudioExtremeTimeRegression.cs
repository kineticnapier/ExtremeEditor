using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

internal static class OfflineAudioExtremeTimeRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        TimingMap timing = TimingMapBuilder.Build(level);
        HitSoundTimeline hits = HitSoundTimelineBuilder.Build(level);
        var renderer = new OfflineAudioRenderer();

        VerifyInvalidRange(-1e100, 0.0,
            nameof(OfflineAudioRenderPlan.StartAudioTimeSeconds));
        VerifyInvalidRange(0.0, 1e100,
            nameof(OfflineAudioRenderPlan.EndAudioTimeSeconds));
        VerifyInvalidRange(1e100, 2e100,
            nameof(OfflineAudioRenderPlan.StartAudioTimeSeconds));

        // A huge chunk size can overflow the stereo sample-count
        // multiplication before any audio is emitted. Reject it during
        // plan validation instead of leaking an OverflowException.
        VerifyInvalidChunkFrames(Array.MaxLength / 2 + 1);
        VerifyInvalidChunkFrames(int.MaxValue);

        // The ffmpeg encoder converts each stereo float frame into eight
        // bytes. A chunk can fit in float[] yet overflow its PCM byte count.
        // Test Validate directly so the pre-fix RED cannot allocate gigabytes.
        int maximumEncodableFrames = Array.MaxLength / (2 * sizeof(float));
        var tooLargeForEncoder = new OfflineAudioRenderPlan(
            level, timing, hits, SongPath: null,
            StartAudioTimeSeconds: 0.0,
            EndAudioTimeSeconds: 1.0 / 48_000,
            HitSoundsEnabled: false,
            ChunkFrames: maximumEncodableFrames + 1);
        try
        {
            tooLargeForEncoder.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
            when (ex.ParamName == nameof(OfflineAudioRenderPlan.ChunkFrames))
        {
            goto EncoderChunkLimitRejected;
        }
        throw new InvalidOperationException(
            "A stereo chunk whose PCM byte count exceeds Array.MaxLength was accepted.");

    EncoderChunkLimitRejected:
        // The exact boundary is a valid plan; do not render a huge buffer.
        (tooLargeForEncoder with { ChunkFrames = maximumEncodableFrames }).Validate();

        // Ordinary one-frame silent exports must remain valid.
        var valid = new OfflineAudioRenderPlan(
            level, timing, hits, SongPath: null,
            StartAudioTimeSeconds: 0.0,
            EndAudioTimeSeconds: 1.0 / 48_000,
            HitSoundsEnabled: false);
        int frames = 0;
        renderer.RenderAsync(valid, (chunk, _) =>
        {
            frames += chunk.FrameCount;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        if (frames != 1)
            throw new InvalidOperationException(
                $"Normal offline time range produced {frames} frames instead of one.");

        // The smallest representable sample index is valid, but its
        // negation is not. A range crossing zero can also exceed the
        // signed 64-bit limit in length. Inspect only the first chunk to
        // avoid rendering an astronomically long silent interval.
        double minimumStartSeconds = -9223372036854775808d / 48_000d;
        foreach (double end in new[] { minimumStartSeconds + 1.0, 0.0 })
        {
            using var stop = new CancellationTokenSource();
            var boundary = new OfflineAudioRenderPlan(
                level, timing, hits, SongPath: null,
                StartAudioTimeSeconds: minimumStartSeconds,
                EndAudioTimeSeconds: end,
                HitSoundsEnabled: false,
                ChunkFrames: 16);
            int chunks = 0;
            try
            {
                renderer.RenderAsync(boundary, (chunk, _) =>
                {
                    if (chunk.StartFrame != long.MinValue ||
                        chunk.FrameCount != 16 ||
                        chunk.SampleRate != 48_000 ||
                        !Array.TrueForAll(chunk.Samples, sample => sample == 0f))
                    {
                        throw new InvalidOperationException(
                            "The minimum-frame export must begin with 16 silent frames.");
                    }

                    chunks++;
                    stop.Cancel();
                    return ValueTask.CompletedTask;
                }, stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Expected after the first chunk; no further chunks are needed.
            }

            if (chunks != 1)
                throw new InvalidOperationException(
                    "The minimum-frame export did not emit its first chunk.");
        }

        void VerifyInvalidChunkFrames(int chunkFrames)
        {
            var plan = new OfflineAudioRenderPlan(
                level, timing, hits, SongPath: null,
                StartAudioTimeSeconds: 0.0,
                EndAudioTimeSeconds: 1.0 / 48_000,
                HitSoundsEnabled: false,
                ChunkFrames: chunkFrames);
            try
            {
                renderer.RenderAsync(plan, (_, _) => ValueTask.CompletedTask,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ArgumentOutOfRangeException ex)
                when (ex.ParamName == nameof(OfflineAudioRenderPlan.ChunkFrames))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Oversized offline audio chunk {chunkFrames} was not rejected during validation.");
        }

        void VerifyInvalidRange(double start, double end, string expectedParameter)
        {
            var plan = new OfflineAudioRenderPlan(
                level, timing, hits, SongPath: null,
                StartAudioTimeSeconds: start,
                EndAudioTimeSeconds: end,
                HitSoundsEnabled: false);
            try
            {
                renderer.RenderAsync(plan, (_, _) => ValueTask.CompletedTask,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ArgumentOutOfRangeException ex) when (ex.ParamName == expectedParameter)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Unrepresentable offline time range {start}..{end} was not rejected as {expectedParameter}.");
        }
    }
}
