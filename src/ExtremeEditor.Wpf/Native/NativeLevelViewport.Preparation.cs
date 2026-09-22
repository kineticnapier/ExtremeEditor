using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct PreparedNativeLevel(
    NativeLevelSnapshot Snapshot,
    NativeLevelSnapshotBuildMetrics BuildMetrics,
    TimeSpan BuildTime);

internal readonly record struct PreparedNativePlayback(
    NativePlaybackTiming[] PlaybackTimeline,
    NativeCameraEvent[] CameraTimeline,
    NativeTrackTransformEvent[] TrackTransformTimeline,
    TimeSpan BuildTime);

public sealed partial class NativeLevelViewport
{
    internal static PreparedNativeLevel PrepareLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var watch = Stopwatch.StartNew();
        NativeLevelSnapshotBuildResult result = FlatNativeLevelSnapshotBuilder.BuildProfiled(level);
        watch.Stop();
        return new PreparedNativeLevel(result.Snapshot, result.Metrics, watch.Elapsed);
    }

    internal static PreparedNativePlayback PreparePlayback(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        var watch = Stopwatch.StartNew();
        NativePlaybackTiming[] playbackTimeline = NativePlaybackTimelineBuilder.Build(timingMap);
        NativeCameraEvent[] cameraTimeline = FaithfulNativeCameraTimelineBuilder.Build(level, timingMap);
        NativeCameraRuntimeCompatibility.MakePlayerStartsRelative(level, timingMap, cameraTimeline);
        StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
        NativeTrackTransformEvent[] trackTransformTimeline = TrackTransformResolver.BuildMoveTimeline(
            level,
            timingMap,
            staticTransforms);
        watch.Stop();

        return new PreparedNativePlayback(
            playbackTimeline,
            cameraTimeline,
            trackTransformTimeline,
            watch.Elapsed);
    }

    internal void SetPreparedLevel(LevelDocument level, PreparedNativeLevel prepared)
    {
        ArgumentNullException.ThrowIfNull(level);

        bool changedDocument = !ReferenceEquals(_level, level);
        _level = level;
        _snapshot = prepared.Snapshot;
        if (changedDocument)
        {
            _selectedFloors = [];
            _primarySelection = -1;
            _cameraTimeline = [];
            _trackTransformTimeline = [];
        }

        var uploadWatch = Stopwatch.StartNew();
        if (_session is not null)
        {
            _session.SetLevel(_snapshot);
            _session.SetSelection(_selectedFloors, _primarySelection);
        }
        uploadWatch.Stop();

        NativeLevelSnapshotBuildMetrics metrics = prepared.BuildMetrics;
        LastLevelUploadMetrics = new NativeLevelUploadMetrics(
            prepared.BuildTime,
            uploadWatch.Elapsed,
            metrics.FloorGeometry,
            metrics.Icons,
            metrics.Finalize,
            metrics.GeometryCount,
            metrics.IconAssetCount,
            metrics.ActionFloorCount);
    }

    internal void SetPreparedPlayback(PreparedNativePlayback prepared)
    {
        _playbackTimeline = prepared.PlaybackTimeline;
        _cameraTimeline = prepared.CameraTimeline;
        _trackTransformTimeline = prepared.TrackTransformTimeline;

        var watch = Stopwatch.StartNew();
        UploadPendingPlaybackTimeline();
        UploadPendingCameraTimeline();
        UploadPendingTrackTransformTimeline();
        watch.Stop();

        LastPlaybackTimelineUploadMetrics = new NativePlaybackTimelineUploadMetrics(
            prepared.BuildTime,
            watch.Elapsed);
    }
}
