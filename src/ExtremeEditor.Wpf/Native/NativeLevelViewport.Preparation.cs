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
    NativeFlashEvent[] FlashTimeline,
    NativeBloomEvent[] BloomTimeline,
    NativeScreenTileEvent[] ScreenTileTimeline,
    NativeFilterEvent[] FilterTimeline,
    NativeTrackTransformEvent[] TrackTransformTimeline,
    NativeTrackVisualEvent[] TrackVisualTimeline,
    NativeTrackAnimationSegment[] TrackAnimationTimeline,
    NativeTrackAnimationTiming[] TrackAnimationTimings,
    TimeSpan BuildTime);

public sealed partial class NativeLevelViewport
{
    internal static PreparedNativeLevel PrepareLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        NativeLevelUpdateDiagnostics.RecordPrepareLevel();

        Console.WriteLine(
            $"[native-prepare] begin floors={level.FloorCount} actions={level.ActionCount} decorations={level.DecorationCount}");

        if (DecorationDiagnostics.Enabled)
            DecorationBlendDiagnostics.Log(level);

        var totalWatch = Stopwatch.StartNew();
        var phaseWatch = Stopwatch.StartNew();
        NativeLevelSnapshotBuildResult result = FlatNativeLevelSnapshotBuilder.BuildProfiled(level);
        phaseWatch.Stop();
        Console.WriteLine($"[native-prepare] snapshot={phaseWatch.Elapsed.TotalMilliseconds:F1}ms");

        phaseWatch.Restart();
        StaticDecorationSnapshotData decorationData = StaticDecorationSnapshotBuilder.Build(level);
        result.Snapshot.StaticDecorations = decorationData.Instances;
        result.Snapshot.DecorationAssets = decorationData.Assets;
        result.Snapshot.DecorationPlayback = decorationData.PlaybackRuntime;
        phaseWatch.Stop();
        Console.WriteLine($"[native-prepare] decorations={phaseWatch.Elapsed.TotalMilliseconds:F1}ms");

        totalWatch.Stop();
        Console.WriteLine($"[native-prepare] total={totalWatch.Elapsed.TotalMilliseconds:F1}ms");
        return new PreparedNativeLevel(result.Snapshot, result.Metrics, totalWatch.Elapsed);
    }

    internal static PreparedNativePlayback PreparePlayback(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        var watch = Stopwatch.StartNew();
        NativePlaybackTiming[] playbackTimeline = NativePlaybackTimelineBuilder.Build(timingMap);
        NativeCameraEvent[] cameraTimeline = FaithfulNativeCameraTimelineBuilder.Build(level, timingMap);
        NativeCameraRuntimeCompatibility.MakePlayerStartsRelative(level, timingMap, cameraTimeline);
        NativeFlashEvent[] flashTimeline = FlashTimelineBuilder.Build(level, timingMap);
        NativeBloomEvent[] bloomTimeline = BloomTimelineBuilder.Build(level, timingMap);
        NativeScreenTileEvent[] screenTileTimeline = ScreenTileTimelineBuilder.Build(level, timingMap);
        NativeFilterEvent[] filterTimeline = FilterTimelineBuilder.Build(level, timingMap);
        StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
        NativeTrackTransformEvent[] trackTransformTimeline = TrackTransformResolver.BuildMoveTimeline(
            level,
            timingMap,
            staticTransforms);
        NativeTrackVisualEvent[] trackVisualTimeline = TrackVisualTimelineBuilder.Build(level, timingMap);
        (NativeTrackAnimationSegment[] trackAnimationTimeline, NativeTrackAnimationTiming[] trackAnimationTimings) =
            TrackAnimationTimelineBuilder.Build(level, timingMap);
        watch.Stop();

        return new PreparedNativePlayback(
            playbackTimeline,
            cameraTimeline,
            flashTimeline,
            bloomTimeline,
            screenTileTimeline,
            filterTimeline,
            trackTransformTimeline,
            trackVisualTimeline,
            trackAnimationTimeline,
            trackAnimationTimings,
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
            _flashTimeline = [];
            _bloomTimeline = [];
            _screenTileTimeline = [];
            _filterTimeline = [];
            _trackTransformTimeline = [];
            _trackVisualTimeline = [];
            _trackAnimationTimeline = [];
            _trackAnimationTimings = [];
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
        _flashTimeline = prepared.FlashTimeline;
        _bloomTimeline = prepared.BloomTimeline;
        _screenTileTimeline = prepared.ScreenTileTimeline;
        _filterTimeline = prepared.FilterTimeline;
        _trackTransformTimeline = prepared.TrackTransformTimeline;
        _trackVisualTimeline = prepared.TrackVisualTimeline;
        _trackAnimationTimeline = prepared.TrackAnimationTimeline;
        _trackAnimationTimings = prepared.TrackAnimationTimings;

        var watch = Stopwatch.StartNew();
        UploadPendingPlaybackTimeline();
        UploadPendingCameraTimeline();
        UploadPendingFlashTimeline();
        UploadPendingBloomTimeline();
        UploadPendingScreenTileTimeline();
        UploadPendingFilterTimeline();
        UploadPendingTrackTransformTimeline();
        UploadPendingTrackVisualTimeline();
        UploadPendingTrackAnimationTimeline();
        watch.Stop();

        LastPlaybackTimelineUploadMetrics = new NativePlaybackTimelineUploadMetrics(
            prepared.BuildTime,
            watch.Elapsed);
    }
}
