using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeRgbFrame(int Width, int Height, byte[] Rgb);

internal readonly record struct NativeDecorationFrameMetrics(
    TimeSpan Evaluation,
    TimeSpan Upload,
    int AnimatedCount,
    bool Uploaded);

internal sealed class NativeHeadlessRendererSession : IDisposable
{
    internal const int DefaultWidth = 320;
    internal const int DefaultHeight = 180;
    private const uint ExpectedBaseApiVersion = NativeRendererSession.ExpectedApiVersion;
    private static readonly bool DecorationFrameMetricsEnabled = string.Equals(
        Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_FRAME_METRICS"),
        "1",
        StringComparison.Ordinal);

    private nint _renderer;
    private NativeStaticDecoration[] _staticDecorations = [];
    private NativeDecorationPlaybackRuntime? _decorationPlayback;
    private NativeDecorationFrameMetrics _lastDecorationFrameMetrics;

    private NativeHeadlessRendererSession(nint renderer, int width, int height)
    {
        _renderer = renderer;
        Width = width;
        Height = height;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal NativeStaticDecoration[] StaticDecorations => _staticDecorations;
    internal NativeDecorationFrameMetrics LastDecorationFrameMetrics => _lastDecorationFrameMetrics;

    internal static NativeHeadlessRendererSession Create(
        LevelDocument level,
        TimingMap timingMap,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ValidateFrameDimensions(width, height);
        return CreateFromPrepared(
            () => NativeLevelViewport.PrepareLevel(level),
            () => NativeLevelViewport.PreparePlayback(level, timingMap),
            width,
            height);
    }

    // Uploads already prepared data. The preparations run after the native
    // renderer exists, matching Create's order of failures.
    internal static NativeHeadlessRendererSession CreateFromPrepared(
        Func<PreparedNativeLevel> prepareLevel,
        Func<PreparedNativePlayback> preparePlayback,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(prepareLevel);
        ArgumentNullException.ThrowIfNull(preparePlayback);
        ValidateFrameDimensions(width, height);

        uint apiVersion = NativeRendererNative.GetApiVersion();
        if (apiVersion != ExpectedBaseApiVersion)
            throw new InvalidOperationException(
                $"Native renderer API version mismatch. Expected {ExpectedBaseApiVersion}, got {apiVersion}.");

        var createInfo = new NativeRendererCreateInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeRendererCreateInfo>()),
            Width = checked((uint)width),
            Height = checked((uint)height),
            Flags = 0u
        };

        int createResult = NativeRendererNative.CreateHeadless(ref createInfo, out nint renderer);
        if (createResult != 0 || renderer == nint.Zero)
            throw new InvalidOperationException(
                $"Native headless renderer creation failed with result {createResult}.");

        var session = new NativeHeadlessRendererSession(renderer, width, height);
        try
        {
            PreparedNativeLevel levelData = prepareLevel();
            PreparedNativePlayback playback = preparePlayback();
            session.SetLevel(levelData.Snapshot);
            session.SetPlayback(playback);
            NativeRendererNative.SetFollowPlayer(renderer, 1);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // This internal factory can be used without NativeChartFrameRenderer.
    // Validate before any native API call; the helper is testable without
    // risking a huge native allocation when the validation regresses.
    internal static void ValidateFrameDimensions(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        _ = VideoExportRgb24Buffer.GetRequiredByteCount(width, height);
    }

    internal NativeRgbFrame Render(double chartTimeSeconds) =>
        Render(chartTimeSeconds, chartTimeSeconds);

    internal void SetCleanView(bool enabled)
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.SetCleanView(_renderer, enabled ? 1 : 0);
    }

    internal NativeRgbFrame Render(double sceneTimeSeconds, double visualTimeSeconds)
    {
        // Reject invalid requests before allocating a potentially large RGB24 frame.
        ValidateRenderRequest(sceneTimeSeconds, visualTimeSeconds);
        int stride = checked(Width * 3);
        byte[] rgb = new byte[checked(stride * Height)];
        RenderInto(sceneTimeSeconds, visualTimeSeconds, rgb);
        return new NativeRgbFrame(Width, Height, rgb);
    }

    internal void RenderInto(
        double sceneTimeSeconds,
        double visualTimeSeconds,
        byte[] rgb)
    {
        ValidateRenderRequest(sceneTimeSeconds, visualTimeSeconds);

        int stride = checked(Width * 3);
        ArgumentNullException.ThrowIfNull(rgb);
        int requiredLength = checked(stride * Height);
        if (rgb.Length != requiredLength)
        {
            throw new ArgumentException(
                $"RGB buffer length must be exactly {requiredLength} bytes.",
                nameof(rgb));
        }
        UpdateDecorationPlayback(sceneTimeSeconds);
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(rgb, GCHandleType.Pinned);
            int result = NativeRendererNative.RenderRgb(
                _renderer,
                sceneTimeSeconds,
                visualTimeSeconds,
                handle.AddrOfPinnedObject(),
                checked((uint)rgb.Length),
                checked((uint)stride));
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native headless RGB render failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }

    }

    internal void ValidateRenderRequest(double sceneTimeSeconds, double visualTimeSeconds)
    {
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeHeadlessRendererSession));
        if (!double.IsFinite(sceneTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(sceneTimeSeconds));
        if (!double.IsFinite(visualTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(visualTimeSeconds));
    }

    private void SetLevel(NativeLevelSnapshot snapshot)
    {
        if (snapshot.Floors.Length == 0 || snapshot.Geometries.Length == 0 || snapshot.Points.Length == 0)
            throw new ArgumentException("Native level snapshot must contain floors and geometry.", nameof(snapshot));

        GCHandle floorsHandle = default;
        GCHandle geometriesHandle = default;
        GCHandle pointsHandle = default;
        try
        {
            floorsHandle = GCHandle.Alloc(snapshot.Floors, GCHandleType.Pinned);
            geometriesHandle = GCHandle.Alloc(snapshot.Geometries, GCHandleType.Pinned);
            pointsHandle = GCHandle.Alloc(snapshot.Points, GCHandleType.Pinned);
            int result = NativeRendererNative.SetLevel(
                _renderer,
                floorsHandle.AddrOfPinnedObject(),
                checked((uint)snapshot.Floors.Length),
                geometriesHandle.AddrOfPinnedObject(),
                checked((uint)snapshot.Geometries.Length),
                pointsHandle.AddrOfPinnedObject(),
                checked((uint)snapshot.Points.Length),
                snapshot.BoundsLeft,
                snapshot.BoundsTop,
                snapshot.BoundsRight,
                snapshot.BoundsBottom);
            if (result != 0)
                throw new InvalidOperationException($"Native level upload failed with result {result}.");
        }
        finally
        {
            if (pointsHandle.IsAllocated) pointsHandle.Free();
            if (geometriesHandle.IsAllocated) geometriesHandle.Free();
            if (floorsHandle.IsAllocated) floorsHandle.Free();
        }

        UploadArray(snapshot.StaticDecorations, NativeRendererNative.SetStaticDecorations, "static-decoration");
        _staticDecorations = snapshot.StaticDecorations;
        _decorationPlayback = snapshot.DecorationPlayback;

        NativeRendererNative.ClearDecorationAssets(_renderer);
        foreach (NativeDecorationAsset asset in snapshot.DecorationAssets)
        {
            int result = NativeRendererNative.SetDecorationAsset(_renderer, asset.Id, asset.ImagePath);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native decoration asset upload failed for {asset.ImagePath} with result {result}.");
        }

        // Export must receive the same background image table as the live viewport.
        int backgroundResult = NativeRendererNative.ReplaceBackgroundAssets(
            _renderer, snapshot.BackgroundAssets);
        if (backgroundResult != 0)
            throw new InvalidOperationException(
                $"Native background asset replacement failed with result {backgroundResult}.");

        NativeRendererNative.ClearIconAssets(_renderer);
        foreach (NativeIconAsset asset in snapshot.IconAssets)
        {
            NativeSpriteMetadata imageMetadata = asset.ImageMetadata;
            NativeSpriteMetadata outlineMetadata = asset.OutlineMetadata;
            int result = NativeRendererNative.SetIconAsset(
                _renderer,
                asset.Id,
                asset.ImagePath,
                asset.OutlinePath,
                ref imageMetadata,
                ref outlineMetadata);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native icon asset upload failed for {asset.ImagePath} with result {result}.");
        }
    }

    private void SetPlayback(PreparedNativePlayback prepared)
    {
        UploadArray(prepared.PlaybackTimeline, NativeRendererNative.SetPlaybackTimeline, "playback");
        UploadArray(prepared.CameraTimeline, NativeRendererNative.SetCameraTimeline, "camera");
        UploadArray(prepared.FlashTimeline, NativeRendererNative.SetFlashTimeline, "flash");
        UploadArray(prepared.BloomTimeline, NativeRendererNative.SetBloomTimeline, "bloom");
        UploadArray(prepared.ScreenTileTimeline, NativeRendererNative.SetScreenTileTimeline, "screen-tile");
        UploadArray(prepared.FilterTimeline, NativeRendererNative.SetFilterTimeline, "filter");
        UploadArray(prepared.BackgroundTimeline, NativeRendererNative.SetBackgroundTimeline, "background");
        UploadArray(prepared.TrackTransformTimeline, NativeRendererNative.SetTrackTransformTimeline, "track-transform");
        UploadArray(prepared.TrackVisualTimeline, NativeRendererNative.SetTrackVisualTimeline, "track-visual");
        UploadTrackAnimation(prepared.TrackAnimationTimeline, prepared.TrackAnimationTimings);
    }

    private void UpdateDecorationPlayback(double chartTime)
    {
        if (_decorationPlayback is not NativeDecorationPlaybackRuntime playback)
        {
            _lastDecorationFrameMetrics = default;
            return;
        }

        bool measure = DecorationFrameMetricsEnabled;
        long evaluationStart = measure ? Stopwatch.GetTimestamp() : 0L;
        bool updated = playback.Update(chartTime);
        TimeSpan evaluation = measure
            ? Stopwatch.GetElapsedTime(evaluationStart)
            : TimeSpan.Zero;
        if (!updated)
        {
            _lastDecorationFrameMetrics = new NativeDecorationFrameMetrics(
                evaluation, TimeSpan.Zero, playback.AnimatedCount, Uploaded: false);
            return;
        }

        try
        {
            long uploadStart = measure ? Stopwatch.GetTimestamp() : 0L;
            UploadArray(
                _staticDecorations,
                NativeRendererNative.SetStaticDecorations,
                "static-decoration playback");
            _lastDecorationFrameMetrics = new NativeDecorationFrameMetrics(
                evaluation,
                measure ? Stopwatch.GetElapsedTime(uploadStart) : TimeSpan.Zero,
                playback.AnimatedCount,
                Uploaded: true);
        }
        catch
        {
            // Match the live viewport: a failed upload must be retried when a
            // paused/exported frame requests the same chart time again.
            playback.InvalidateLastTime();
            throw;
        }
    }

    private delegate int NativeArraySetter(nint renderer, nint pointer, uint count);

    private void UploadArray<T>(T[] values, NativeArraySetter setter, string label) where T : struct
    {
        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (values.Length > 0)
            {
                handle = GCHandle.Alloc(values, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }
            int result = setter(_renderer, pointer, checked((uint)values.Length));
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native {label} upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    private void UploadTrackAnimation(
        NativeTrackAnimationSegment[] segments,
        NativeTrackAnimationTiming[] timings)
    {
        GCHandle segmentHandle = default;
        GCHandle timingHandle = default;
        try
        {
            nint segmentPointer = nint.Zero;
            nint timingPointer = nint.Zero;
            if (segments.Length > 0)
            {
                segmentHandle = GCHandle.Alloc(segments, GCHandleType.Pinned);
                segmentPointer = segmentHandle.AddrOfPinnedObject();
            }
            if (timings.Length > 0)
            {
                timingHandle = GCHandle.Alloc(timings, GCHandleType.Pinned);
                timingPointer = timingHandle.AddrOfPinnedObject();
            }

            int result = NativeRendererNative.SetTrackAnimationTimeline(
                _renderer,
                segmentPointer,
                checked((uint)segments.Length),
                timingPointer,
                checked((uint)timings.Length));
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native track-animation upload failed with result {result}.");
        }
        finally
        {
            if (timingHandle.IsAllocated) timingHandle.Free();
            if (segmentHandle.IsAllocated) segmentHandle.Free();
        }
    }

    public void Dispose()
    {
        nint renderer = _renderer;
        if (renderer == nint.Zero)
            return;
        _renderer = nint.Zero;
        _decorationPlayback = null;
        _staticDecorations = [];
        _lastDecorationFrameMetrics = default;
        NativeRendererNative.Destroy(renderer);
        GC.SuppressFinalize(this);
    }
}
