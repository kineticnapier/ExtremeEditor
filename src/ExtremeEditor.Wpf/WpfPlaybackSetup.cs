using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed record WpfPlaybackSetup(
    TimingMap TimingMap,
    HitSoundTimeline HitSoundTimeline,
    string? SongPath);

internal static class WpfPlaybackSetupBuilder
{
    public static WpfPlaybackSetup Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        return new WpfPlaybackSetup(
            TimingMapBuilder.Build(level),
            HitSoundTimelineBuilder.Build(level),
            level.ResolveSongPath());
    }
}
