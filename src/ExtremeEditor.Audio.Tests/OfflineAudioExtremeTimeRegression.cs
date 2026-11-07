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
