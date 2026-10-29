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
    NativeBackgroundEvent[] BackgroundTimeline,
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
        var (backgroundAssets, backgroundIds) = BackgroundAssetResolver.Build(level);
        BackgroundSourceState initialBackground = BackgroundSourceReader.ReadInitial(level);
        result.Snapshot.BackgroundState = BackgroundNativeEncoding.Encode(initialBackground,
            backgroundIds.TryGetValue(initialBackground.Image, out uint backgroundId) ? backgroundId : 0u);
        result.Snapshot.BackgroundAssets = backgroundAssets;
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
        NativeBackgroundEvent[] backgroundTimeline = BackgroundTimelineBuilder.Build(level, timingMap);
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
            backgroundTimeline,
            trackTransformTimeline,
            trackVisualTimeline,
            trackAnimationTimeline,
            trackAnimationTimings,
            watch.Elapsed);
    }

    internal void SetPreparedLevel(LevelDocument level, PreparedNativeLevel prepared)
    {
        SetPreparedLevelCore(level, prepared, (snapshot, selectedFloors, primaryFloor) =>
        {
            if (_session is not null)
            {
                _session.SetLevel(snapshot, useCache: true);
                _session.SetSelection(selectedFloors, primaryFloor);
            }
        });
    }

    // A native upload may throw after changing part of the renderer. Do not
    // commit the viewport's managed document/snapshot until both uploads pass:
    // the caller can then attempt rollback using the previous managed scene.
    // The upload delegate also permits fault injection without a native HWND.
    internal void SetPreparedLevelCore(
        LevelDocument level,
        PreparedNativeLevel prepared,
        Action<NativeLevelSnapshot, int[], int> upload)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(upload);
        _playbackAnchorGate.Invalidate();

        bool changedDocument = !ReferenceEquals(_level, level);
        int[] selectedFloors = changedDocument ? [] : _selectedFloors;
        int primaryFloor = changedDocument ? -1 : _primarySelection;

        var uploadWatch = Stopwatch.StartNew();
        upload(prepared.Snapshot, selectedFloors, primaryFloor);
        uploadWatch.Stop();

        // Only a successful upload may replace the active managed scene.
        _level = level;
        _snapshot = prepared.Snapshot;
        if (changedDocument)
        {
            _selectedFloors = selectedFloors;
            _primarySelection = primaryFloor;
            _cameraTimeline = [];
            _flashTimeline = [];
            _bloomTimeline = [];
            _screenTileTimeline = [];
            _filterTimeline = [];
            _backgroundTimeline = [];
            _trackTransformTimeline = [];
            _trackVisualTimeline = [];
            _trackAnimationTimeline = [];
            _trackAnimationTimings = [];
        }

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
        SetPreparedPlaybackCore(prepared, () =>
        {
            UploadPendingPlaybackTimeline();
            UploadPendingCameraTimeline();
            UploadPendingFlashTimeline();
            UploadPendingBloomTimeline();
            UploadPendingScreenTileTimeline();
            UploadPendingFilterTimeline();
            UploadPendingBackgroundTimeline();
            UploadPendingTrackTransformTimeline();
            UploadPendingTrackVisualTimeline();
            UploadPendingTrackAnimationTimeline();
        });
    }

    // The native renderer can fail after a partial timeline upload. Preserve
    // the previous managed timelines so the owning tab can retry/restore them.
    // Invalidate the camera upload cache on failure: even the camera call may
    // have partially written native state before throwing.
    internal void SetPreparedPlaybackCore(PreparedNativePlayback prepared, Action upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        _playbackAnchorGate.Invalidate();

        NativePlaybackTiming[] previousPlayback = _playbackTimeline;
        NativeCameraEvent[] previousCamera = _cameraTimeline;
        NativeFlashEvent[] previousFlash = _flashTimeline;
        NativeBloomEvent[] previousBloom = _bloomTimeline;
        NativeScreenTileEvent[] previousScreenTile = _screenTileTimeline;
        NativeFilterEvent[] previousFilter = _filterTimeline;
        NativeBackgroundEvent[] previousBackground = _backgroundTimeline;
        NativeTrackTransformEvent[] previousTrackTransform = _trackTransformTimeline;
        NativeTrackVisualEvent[] previousTrackVisual = _trackVisualTimeline;
        NativeTrackAnimationSegment[] previousTrackAnimation = _trackAnimationTimeline;
        NativeTrackAnimationTiming[] previousTrackTimings = _trackAnimationTimings;

        _playbackTimeline = prepared.PlaybackTimeline;
        _cameraTimeline = prepared.CameraTimeline;
        _flashTimeline = prepared.FlashTimeline;
        _bloomTimeline = prepared.BloomTimeline;
        _screenTileTimeline = prepared.ScreenTileTimeline;
        _filterTimeline = prepared.FilterTimeline;
        _backgroundTimeline = prepared.BackgroundTimeline;
        _trackTransformTimeline = prepared.TrackTransformTimeline;
        _trackVisualTimeline = prepared.TrackVisualTimeline;
        _trackAnimationTimeline = prepared.TrackAnimationTimeline;
        _trackAnimationTimings = prepared.TrackAnimationTimings;

        var watch = Stopwatch.StartNew();
        try
        {
            upload();
        }
        catch
        {
            _playbackTimeline = previousPlayback;
            _cameraTimeline = previousCamera;
            _flashTimeline = previousFlash;
            _bloomTimeline = previousBloom;
            _screenTileTimeline = previousScreenTile;
            _filterTimeline = previousFilter;
            _backgroundTimeline = previousBackground;
            _trackTransformTimeline = previousTrackTransform;
            _trackVisualTimeline = previousTrackVisual;
            _trackAnimationTimeline = previousTrackAnimation;
            _trackAnimationTimings = previousTrackTimings;
            _cameraUploadSession = null;
            _cameraUploadedSource = null;
            _cameraUploadedHeight = 0;
            throw;
        }
        watch.Stop();

        LastPlaybackTimelineUploadMetrics = new NativePlaybackTimelineUploadMetrics(
            prepared.BuildTime,
            watch.Elapsed);
    }
}
