using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal readonly record struct NativeLevelLoadMetrics(
    TimeSpan SnapshotBuild,
    TimeSpan Upload,
    TimeSpan FloorGeometry,
    TimeSpan Icons,
    TimeSpan Finalize,
    int GeometryCount,
    int IconAssetCount,
    int ActionFloorCount);

internal readonly record struct NativePlaybackLoadMetrics(
    TimeSpan TimelineBuild,
    TimeSpan Upload);

internal static class NativeLevelViewportProfilingExtensions
{
    internal static NativeLevelLoadMetrics SetLevelProfiled(
        this NativeLevelViewport viewport,
        LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);

        viewport.SetLevel(level);
        NativeLevelUploadMetrics metrics = viewport.LastLevelUploadMetrics;
        return new NativeLevelLoadMetrics(
            metrics.SnapshotBuild,
            metrics.NativeUpload,
            metrics.SnapshotFloorGeometry,
            metrics.SnapshotIcons,
            metrics.SnapshotFinalize,
            metrics.GeometryCount,
            metrics.IconAssetCount,
            metrics.ActionFloorCount);
    }

    internal static NativePlaybackLoadMetrics SetPlaybackTimelineProfiled(
        this NativeLevelViewport viewport,
        TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(timingMap);

        viewport.SetPlaybackTimeline(timingMap);
        NativePlaybackTimelineUploadMetrics metrics = viewport.LastPlaybackTimelineUploadMetrics;
        return new NativePlaybackLoadMetrics(metrics.TimelineBuild, metrics.NativeUpload);
    }
}
