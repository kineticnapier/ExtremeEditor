using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed record WpfPlaybackSetup(
    TimingMap TimingMap,
    HitSoundTimeline HitSoundTimeline,
    string? SongPath,
    TimeSpan TimingTime,
    TimeSpan HitSoundTime,
    TimeSpan ResolveSongTime);

internal static class WpfPlaybackSetupBuilder
{
    public static WpfPlaybackSetup Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var watch = Stopwatch.StartNew();
        TimingMap timingMap = FlatTimingMapBuilder.Build(level);
        watch.Stop();
        TimeSpan timingMapTime = watch.Elapsed;

        watch.Restart();
        HitSoundTimeline hitSoundTimeline = HitSoundTimelineBuilder.Build(level);
        watch.Stop();
        TimeSpan hitSoundTimelineTime = watch.Elapsed;

        watch.Restart();
        string? songPath = level.ResolveSongPath();
        watch.Stop();
        TimeSpan resolveSongTime = watch.Elapsed;

        // Editor-visible native geometry is on the initial critical path. Give it
        // the worker pool first; playback timeline/audio preparation starts only
        // after the editor-ready state has been published.
        _ = NativeLoadPreparationCache.StartLevel(level);

        return new WpfPlaybackSetup(
            timingMap,
            hitSoundTimeline,
            songPath,
            timingMapTime,
            hitSoundTimelineTime,
            resolveSongTime);
    }
}
