using System.Runtime.InteropServices;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeRgbFrame(int Width, int Height, byte[] Rgb);

internal sealed class NativeHeadlessRendererSession : IDisposable
{
    internal const int DefaultWidth = 320;
    internal const int DefaultHeight = 180;
    private const uint ExpectedBaseApiVersion = 20;

    private nint _renderer;

    private NativeHeadlessRendererSession(nint renderer, int width, int height)
    {
        _renderer = renderer;
        Width = width;
        Height = height;
    }

    internal int Width { get; }
    internal int Height { get; }

    internal static NativeHeadlessRendererSession Create(
        LevelDocument level,
        TimingMap timingMap,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

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
            PreparedNativeLevel levelData = NativeLevelViewport.PrepareLevel(level);
            PreparedNativePlayback playback = NativeLevelViewport.PreparePlayback(level, timingMap);
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

    internal NativeRgbFrame Render(double chartTimeSeconds) =>
        Render(chartTimeSeconds, chartTimeSeconds);

    internal NativeRgbFrame Render(double sceneTimeSeconds, double visualTimeSeconds)
    {
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeHeadlessRendererSession));
        if (!double.IsFinite(sceneTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(sceneTimeSeconds));
        if (!double.IsFinite(visualTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(visualTimeSeconds));

        int stride = checked(Width * 3);
        byte[] rgb = new byte[checked(stride * Height)];
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

        return new NativeRgbFrame(Width, Height, rgb);
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

        NativeRendererNative.ClearDecorationAssets(_renderer);
        foreach (NativeDecorationAsset asset in snapshot.DecorationAssets)
        {
            int result = NativeRendererNative.SetDecorationAsset(_renderer, asset.Id, asset.ImagePath);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native decoration asset upload failed for {asset.ImagePath} with result {result}.");
        }

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
        UploadArray(prepared.TrackTransformTimeline, NativeRendererNative.SetTrackTransformTimeline, "track-transform");
        UploadArray(prepared.TrackVisualTimeline, NativeRendererNative.SetTrackVisualTimeline, "track-visual");
        UploadTrackAnimation(prepared.TrackAnimationTimeline, prepared.TrackAnimationTimings);
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
        NativeRendererNative.Destroy(renderer);
        GC.SuppressFinalize(this);
    }
}
