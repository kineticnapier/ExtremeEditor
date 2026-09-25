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
    internal static PreparedNativeLevel PrepareLevelProfiled(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return NativeLevelViewport.PrepareLevel(level);
    }

    internal static PreparedNativePlayback PreparePlaybackTimelineProfiled(
        LevelDocument level,
        TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        return NativeLevelViewport.PreparePlayback(level, timingMap);
    }

    internal static NativeLevelLoadMetrics SetPreparedLevelProfiled(
        this NativeLevelViewport viewport,
        LevelDocument level,
        PreparedNativeLevel prepared)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);

        viewport.SetPreparedLevel(level, prepared);
        return ReadLevelMetrics(viewport);
    }

    internal static NativePlaybackLoadMetrics SetPreparedPlaybackTimelineProfiled(
        this NativeLevelViewport viewport,
        PreparedNativePlayback prepared)
    {
        ArgumentNullException.ThrowIfNull(viewport);

        viewport.SetPreparedPlayback(prepared);
        NativePlaybackTimelineUploadMetrics metrics = viewport.LastPlaybackTimelineUploadMetrics;
        return new NativePlaybackLoadMetrics(metrics.TimelineBuild, metrics.NativeUpload);
    }

    internal static NativeLevelLoadMetrics SetLevelProfiled(
        this NativeLevelViewport viewport,
        LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);

        if (NativeLoadPreparationCache.TryGetLevel(
                level,
                out Task<PreparedNativeLevel>? preparation) &&
            preparation is not null)
        {
            return viewport.SetPreparedLevelProfiled(
                level,
                preparation.GetAwaiter().GetResult());
        }

        viewport.SetLevel(level);
        return ReadLevelMetrics(viewport);
    }

    internal static NativePlaybackLoadMetrics SetPlaybackTimelineProfiled(
        this NativeLevelViewport viewport,
        TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(timingMap);

        if (NativeLoadPreparationCache.TryGetPlayback(
                timingMap,
                out Task<PreparedNativePlayback>? preparation) &&
            preparation is not null)
        {
            return viewport.SetPreparedPlaybackTimelineProfiled(
                preparation.GetAwaiter().GetResult());
        }

        viewport.SetPlaybackTimeline(timingMap);
        NativePlaybackTimelineUploadMetrics metrics = viewport.LastPlaybackTimelineUploadMetrics;
        return new NativePlaybackLoadMetrics(metrics.TimelineBuild, metrics.NativeUpload);
    }

    private static NativeLevelLoadMetrics ReadLevelMetrics(NativeLevelViewport viewport)
    {
        NativeLevelUploadMetrics metrics = viewport.LastLevelUploadMetrics;
        Console.WriteLine(
            $"[load] native-detail floorGeometry={metrics.SnapshotFloorGeometry.TotalMilliseconds:N1}ms " +
            $"icons={metrics.SnapshotIcons.TotalMilliseconds:N1}ms " +
            $"finalize={metrics.SnapshotFinalize.TotalMilliseconds:N1}ms " +
            $"geometries={metrics.GeometryCount:N0} " +
            $"actionFloors={metrics.ActionFloorCount:N0} " +
            $"iconAssets={metrics.IconAssetCount:N0}");

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
}
