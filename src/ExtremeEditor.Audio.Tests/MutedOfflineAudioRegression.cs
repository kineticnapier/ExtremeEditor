using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

internal static class MutedOfflineAudioRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var renderer = new OfflineAudioRenderer();
        FieldInfo field = typeof(OfflineAudioRenderer).GetField(
            "_hitSoundLibrary", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Offline hit-sound cache field missing.");
        if (field.GetValue(renderer) is not Lazy<HitSoundLibrary> library)
            throw new InvalidOperationException("Offline hit-sound assets must be loaded lazily.");

        if (renderer.ProbeFormat(null).SampleRate != 48_000 || library.IsValueCreated)
            throw new InvalidOperationException("Probing a missing song loaded hit-sound assets.");

        LevelDocument level = LevelDocument.CreateSynthetic(2);
        TimingMap timing = TimingMapBuilder.Build(level);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var plan = new OfflineAudioRenderPlan(
            level, timing, timeline, SongPath: null,
            StartAudioTimeSeconds: 0, EndAudioTimeSeconds: 0.01,
            HitSoundsEnabled: false, ChunkFrames: 7);

        long nextFrame = 0;
        renderer.RenderAsync(plan, (chunk, _) =>
        {
            if (chunk.StartFrame != nextFrame || chunk.SampleRate != 48_000 ||
                chunk.Channels != 2 || chunk.FrameCount <= 0 ||
                chunk.Samples.Any(sample => sample != 0f))
            {
                throw new InvalidOperationException(
                    "Muted offline rendering changed its silent output or frame clock.");
            }

            nextFrame += chunk.FrameCount;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

        if (nextFrame != 480)
            throw new InvalidOperationException(
                $"Muted offline rendering produced {nextFrame} frames instead of 480.");
        if (library.IsValueCreated)
            throw new InvalidOperationException(
                "Muted offline rendering unnecessarily initialized hit-sound assets.");
    }
}
