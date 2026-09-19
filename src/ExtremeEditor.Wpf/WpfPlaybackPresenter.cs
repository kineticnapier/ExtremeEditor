using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal static class WpfPlaybackPresenter
{
    public static void Update(
        LevelViewport viewport,
        LevelDocument level,
        TimingMap timingMap,
        double audioSeconds,
        bool isStopped)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        if (isStopped)
        {
            viewport.SetPlaybackPose(null);
            return;
        }

        double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
        viewport.SetPlaybackPose(timingMap.GetPose(level, chartTime));
    }
}
