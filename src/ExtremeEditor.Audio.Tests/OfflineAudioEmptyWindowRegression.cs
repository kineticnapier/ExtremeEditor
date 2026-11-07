using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

internal static class OfflineAudioEmptyWindowRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        TimingMap timing = TimingMapBuilder.Build(level);
        HitSoundTimeline hits = HitSoundTimelineBuilder.Build(level);

        foreach ((double start, double end) in new[]
        {
            (0.0, 0.000000001),
            (0.001, 0.001000001)
        })
        {
            var renderer = new OfflineAudioRenderer();
            FieldInfo field = typeof(OfflineAudioRenderer).GetField(
                "_hitSoundLibrary", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Offline hit-sound cache field missing.");
            if (field.GetValue(renderer) is not Lazy<HitSoundLibrary> library)
                throw new InvalidOperationException("Offline hit-sound assets must be loaded lazily.");

            var plan = new OfflineAudioRenderPlan(
                level, timing, hits, SongPath: null,
                StartAudioTimeSeconds: start,
                EndAudioTimeSeconds: end,
                HitSoundsEnabled: true);

            int chunks = 0;
            renderer.RenderAsync(plan, (_, _) =>
            {
                chunks++;
                return ValueTask.CompletedTask;
            }, CancellationToken.None).GetAwaiter().GetResult();

            if (chunks != 0)
                throw new InvalidOperationException(
                    "A sub-frame offline audio interval unexpectedly produced PCM.");
            if (library.IsValueCreated)
                throw new InvalidOperationException(
                    "An empty offline audio interval unnecessarily loaded hit-sound assets.");
        }
    }
}
