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

        return new WpfPlaybackSetup(
            timingMap,
            hitSoundTimeline,
            songPath,
            timingMapTime,
            hitSoundTimelineTime,
            watch.Elapsed);
    }
}
