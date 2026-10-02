using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal enum NativeEditorAction : uint
{
    None = 0,
    InsertAngle = 1,
    Delete = 2,
    Rotate180 = 3,
    InsertMidspin = 4,
    InsertFullTurn = 5
}

internal readonly record struct NativeEditorActionRequest(
    NativeEditorAction Action,
    int Floor,
    double Value);

internal sealed class NativeRendererSession : IDisposable
{
    private const uint ExpectedApiVersion = 20;

    private readonly NativeRendererNative.SelectionChangedCallback _selectionChangedCallback;
    private readonly NativeRendererNative.FollowPlayerChangedCallback _followPlayerChangedCallback;
    private readonly NativeRendererNative.EditorActionCallback _editorActionCallback;
    private nint _renderer;

    private NativeRendererSession(nint renderer, nint childHwnd)
    {
        _renderer = renderer;
        ChildHwnd = childHwnd;
        _selectionChangedCallback = OnNativeSelectionChanged;
        _followPlayerChangedCallback = OnNativeFollowPlayerChanged;
        _editorActionCallback = OnNativeEditorAction;
        NativeRendererNative.SetSelectionChangedCallback(_renderer, _selectionChangedCallback, nint.Zero);
        NativeRendererNative.SetFollowPlayerChangedCallback(_renderer, _followPlayerChangedCallback, nint.Zero);
        NativeRendererNative.SetEditorActionCallback(_renderer, _editorActionCallback, nint.Zero);
    }

    internal nint ChildHwnd { get; private set; }
    internal int SelectedFloor => _renderer == nint.Zero ? -1 : NativeRendererNative.GetSelectedFloor(_renderer);
    internal event Action<int, uint>? SelectionChanged;
    internal event Action<bool>? FollowPlayerChanged;
    internal event Action<NativeEditorActionRequest>? EditorActionRequested;

    internal static NativeRendererSession Create(nint parentHwnd, uint width, uint height)
    {
        if (parentHwnd == nint.Zero)
            throw new ArgumentException("Parent HWND must not be zero.", nameof(parentHwnd));

        uint apiVersion = NativeRendererNative.GetApiVersion();
        if (apiVersion != ExpectedApiVersion)
            throw new InvalidOperationException(
                $"Native renderer API version mismatch. Expected {ExpectedApiVersion}, got {apiVersion}.");

        var abiInfo = new NativeAbiInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeAbiInfo>())
        };
        int abiResult = NativeRendererNative.GetAbiInfo(ref abiInfo);
        if (abiResult != 0)
            throw new InvalidOperationException($"Native renderer ABI query failed with result {abiResult}.");

        uint managedFloorSize = checked((uint)Marshal.SizeOf<NativeFloor>());
        if (abiInfo.FloorSize != managedFloorSize)
            throw new InvalidOperationException(
                $"Native floor ABI mismatch. Managed {managedFloorSize}, native {abiInfo.FloorSize}.");

        uint managedFloorIconStateSize = checked((uint)Marshal.SizeOf<NativeFloorIconState>());
        if (abiInfo.FloorIconStateSize != managedFloorIconStateSize)
            throw new InvalidOperationException(
                $"Native floor-icon state ABI mismatch. Managed {managedFloorIconStateSize}, native {abiInfo.FloorIconStateSize}.");

        uint managedTimingSize = checked((uint)Marshal.SizeOf<NativePlaybackTiming>());
        if (abiInfo.ClockSize != managedTimingSize)
            throw new InvalidOperationException(
                $"Native playback timing ABI mismatch. Managed {managedTimingSize}, native {abiInfo.ClockSize}.");

        uint managedDiagnosticsSize = checked((uint)Marshal.SizeOf<NativeRendererDiagnostics>());
        if (abiInfo.DiagnosticsSize != managedDiagnosticsSize)
            throw new InvalidOperationException(
                $"Native diagnostics ABI mismatch. Managed {managedDiagnosticsSize}, native {abiInfo.DiagnosticsSize}.");

        uint managedTrackTransformEventSize = checked((uint)Marshal.SizeOf<NativeTrackTransformEvent>());
        if (abiInfo.TrackTransformEventSize != managedTrackTransformEventSize)
            throw new InvalidOperationException(
                $"Native track-transform ABI mismatch. Managed {managedTrackTransformEventSize}, native {abiInfo.TrackTransformEventSize}.");

        uint managedTrackVisualEventSize = checked((uint)Marshal.SizeOf<NativeTrackVisualEvent>());
        if (abiInfo.TrackVisualEventSize != managedTrackVisualEventSize)
            throw new InvalidOperationException(
                $"Native track-visual ABI mismatch. Managed {managedTrackVisualEventSize}, native {abiInfo.TrackVisualEventSize}.");

        uint managedTrackAnimationSegmentSize = checked((uint)Marshal.SizeOf<NativeTrackAnimationSegment>());
        if (abiInfo.TrackAnimationSegmentSize != managedTrackAnimationSegmentSize)
            throw new InvalidOperationException(
                $"Native track-animation segment ABI mismatch. Managed {managedTrackAnimationSegmentSize}, native {abiInfo.TrackAnimationSegmentSize}.");

        uint managedTrackAnimationTimingSize = checked((uint)Marshal.SizeOf<NativeTrackAnimationTiming>());
        if (abiInfo.TrackAnimationTimingSize != managedTrackAnimationTimingSize)
            throw new InvalidOperationException(
                $"Native track-animation timing ABI mismatch. Managed {managedTrackAnimationTimingSize}, native {abiInfo.TrackAnimationTimingSize}.");

        uint managedCameraEventSize = checked((uint)Marshal.SizeOf<NativeCameraEvent>());
        if (abiInfo.CameraEventSize != managedCameraEventSize)
            throw new InvalidOperationException(
                $"Native camera-event ABI mismatch. Managed {managedCameraEventSize}, native {abiInfo.CameraEventSize}.");

        uint managedSpriteMetadataSize = checked((uint)Marshal.SizeOf<NativeSpriteMetadata>());
        if (abiInfo.SpriteMetadataSize != managedSpriteMetadataSize)
            throw new InvalidOperationException(
                $"Native sprite-metadata ABI mismatch. Managed {managedSpriteMetadataSize}, native {abiInfo.SpriteMetadataSize}.");

        uint managedStaticDecorationSize = checked((uint)Marshal.SizeOf<NativeStaticDecoration>());
        if (abiInfo.StaticDecorationSize != managedStaticDecorationSize)
            throw new InvalidOperationException(
                $"Native static-decoration ABI mismatch. Managed {managedStaticDecorationSize}, native {abiInfo.StaticDecorationSize}.");

        var createInfo = new NativeRendererCreateInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeRendererCreateInfo>()),
            Width = Math.Max(1u, width),
            Height = Math.Max(1u, height),
            Flags = 0u
        };

        int result = NativeRendererNative.Create(parentHwnd, ref createInfo, out nint renderer);
        if (result != 0 || renderer == nint.Zero)
            throw new InvalidOperationException($"Native renderer creation failed with result {result}.");

        nint childHwnd = NativeRendererNative.GetChildHwnd(renderer);
        if (childHwnd == nint.Zero)
        {
            NativeRendererNative.Destroy(renderer);
            throw new InvalidOperationException("Native renderer did not create a child HWND.");
        }

        return new NativeRendererSession(renderer, childHwnd);
    }

    internal void Resize(uint width, uint height)
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.Resize(_renderer, Math.Max(1u, width), Math.Max(1u, height));
    }

    internal void SetLevel(NativeLevelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));
        if (snapshot.Floors.Length == 0 || snapshot.Geometries.Length == 0 || snapshot.Points.Length == 0)
            throw new ArgumentException("Native level snapshot must contain floors and geometry.", nameof(snapshot));

        var totalWatch = Stopwatch.StartNew();
        var phaseWatch = Stopwatch.StartNew();
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
                floorsHandle.AddrOfPinnedObject(), checked((uint)snapshot.Floors.Length),
                geometriesHandle.AddrOfPinnedObject(), checked((uint)snapshot.Geometries.Length),
                pointsHandle.AddrOfPinnedObject(), checked((uint)snapshot.Points.Length),
                snapshot.BoundsLeft, snapshot.BoundsTop, snapshot.BoundsRight, snapshot.BoundsBottom);
            if (result != 0)
                throw new InvalidOperationException($"Native level upload failed with result {result}.");
        }
        finally
        {
            if (pointsHandle.IsAllocated) pointsHandle.Free();
            if (geometriesHandle.IsAllocated) geometriesHandle.Free();
            if (floorsHandle.IsAllocated) floorsHandle.Free();
        }

        phaseWatch.Stop();
        TimeSpan nativeLevelTime = phaseWatch.Elapsed;
        phaseWatch.Restart();
        SetStaticDecorations(snapshot.StaticDecorations, logUpload: true);
        phaseWatch.Stop();
        TimeSpan decorationTime = phaseWatch.Elapsed;

        phaseWatch.Restart();
        NativeRendererNative.ClearDecorationAssets(_renderer);
        foreach (NativeDecorationAsset asset in snapshot.DecorationAssets)
        {
            int result = NativeRendererNative.SetDecorationAsset(_renderer, asset.Id, asset.ImagePath);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native decoration asset upload failed for {asset.ImagePath} with result {result}.");
        }
        phaseWatch.Stop();
        TimeSpan decorationAssetTime = phaseWatch.Elapsed;

        phaseWatch.Restart();
        NativeRendererNative.ClearIconAssets(_renderer);
        foreach (NativeIconAsset asset in snapshot.IconAssets)
        {
            NativeSpriteMetadata imageMetadata = asset.ImageMetadata;
            NativeSpriteMetadata outlineMetadata = asset.OutlineMetadata;
            int result = NativeRendererNative.SetIconAsset(
                _renderer, asset.Id, asset.ImagePath, asset.OutlinePath,
                ref imageMetadata, ref outlineMetadata);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native icon asset upload failed for {asset.ImagePath} with result {result}.");
        }
        phaseWatch.Stop();
        TimeSpan iconAssetTime = phaseWatch.Elapsed;
        totalWatch.Stop();

        if (NativeUploadDiagnostics.Enabled)
        {
            Console.WriteLine(
                $"[native-upload] level={nativeLevelTime.TotalMilliseconds:N1}ms " +
                $"decorations={decorationTime.TotalMilliseconds:N1}ms " +
                $"decorationAssets={decorationAssetTime.TotalMilliseconds:N1}ms " +
                $"iconAssets={iconAssetTime.TotalMilliseconds:N1}ms " +
                $"decorationAssetCount={snapshot.DecorationAssets.Length} " +
                $"iconAssetCount={snapshot.IconAssets.Length} " +
                $"total={totalWatch.Elapsed.TotalMilliseconds:N1}ms");
        }
    }

    internal void SetStaticDecorations(NativeStaticDecoration[] decorations, bool logUpload = false)
    {
        ArgumentNullException.ThrowIfNull(decorations);
        if (_renderer == nint.Zero)
            return;

        if (logUpload && DecorationDiagnostics.Enabled)
            Console.WriteLine($"[decoration-diagnostic] setStaticDecorationsCount={decorations.Length}");

        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (decorations.Length > 0)
            {
                handle = GCHandle.Alloc(decorations, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }
            int result = NativeRendererNative.SetStaticDecorations(
                _renderer, pointer, checked((uint)decorations.Length));
            if (result != 0)
                throw new InvalidOperationException($"Native static-decoration upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    internal void SetSelection(int[] floors, int primaryFloor)
    {
        if (_renderer == nint.Zero)
            return;
        floors ??= [];
        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (floors.Length > 0)
            {
                handle = GCHandle.Alloc(floors, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }
            NativeRendererNative.SetSelection(_renderer, pointer, checked((uint)floors.Length), primaryFloor);
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    internal void SetPlaybackTimeline(NativePlaybackTiming[] timings) =>
        UploadArray(timings, NativeRendererNative.SetPlaybackTimeline, "playback");

    internal void SetCameraTimeline(NativeCameraEvent[] events) =>
        UploadArray(events, NativeRendererNative.SetCameraTimeline, "camera");

    internal void SetTrackTransformTimeline(NativeTrackTransformEvent[] events) =>
        UploadArray(events, NativeRendererNative.SetTrackTransformTimeline, "track-transform");

    internal void SetTrackVisualTimeline(NativeTrackVisualEvent[] events) =>
        UploadArray(events, NativeRendererNative.SetTrackVisualTimeline, "track-visual");

    internal void SetTrackAnimationTimeline(
        NativeTrackAnimationSegment[] segments,
        NativeTrackAnimationTiming[] timings)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(timings);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));

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
                throw new InvalidOperationException($"Native track-animation timeline upload failed with result {result}.");
        }
        finally
        {
            if (timingHandle.IsAllocated) timingHandle.Free();
            if (segmentHandle.IsAllocated) segmentHandle.Free();
        }
    }

    private delegate int NativeArraySetter(nint renderer, nint pointer, uint count);

    private void UploadArray<T>(T[] values, NativeArraySetter setter, string label) where T : struct
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));

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
                throw new InvalidOperationException($"Native {label} timeline upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    internal void SetPlaybackAnchor(double chartTime, double chartRate, bool active, bool playing)
    {
        if (_renderer == nint.Zero)
            return;
        uint flags = 0u;
        if (active) flags |= NativeRendererNative.PlaybackFlagActive;
        if (active && playing) flags |= NativeRendererNative.PlaybackFlagPlaying;
        NativeRendererNative.SetPlaybackAnchor(_renderer, chartTime, chartRate, flags);
        NativeRendererNative.SetTrackPlaybackAnchor(_renderer, chartTime, chartRate, flags);
    }

    internal void SetFollowPlayer(bool enabled)
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.SetFollowPlayer(_renderer, enabled ? 1 : 0);
    }

    internal bool TryGetDiagnostics(out NativeRendererDiagnostics diagnostics)
    {
        diagnostics = new NativeRendererDiagnostics
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeRendererDiagnostics>())
        };
        return _renderer != nint.Zero &&
               NativeRendererNative.GetDiagnostics(_renderer, ref diagnostics) == 0;
    }

    internal void FrameAll()
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.FrameAll(_renderer);
    }

    internal bool UpdateFloorIcons(int startFloor, NativeFloorIconState[] states)
    {
        if (_renderer == nint.Zero || startFloor < 0 || states.Length == 0)
            return false;
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(states, GCHandleType.Pinned);
            return NativeRendererNative.UpdateFloorIcons(
                _renderer, checked((uint)startFloor), handle.AddrOfPinnedObject(),
                checked((uint)states.Length)) == 0;
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    internal void CenterAt(float worldX, float worldY)
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.CenterAt(_renderer, worldX, worldY);
    }

    private void OnNativeSelectionChanged(nint userData, int floor, uint modifiers) =>
        SelectionChanged?.Invoke(floor, modifiers);

    private void OnNativeFollowPlayerChanged(nint userData, int enabled) =>
        FollowPlayerChanged?.Invoke(enabled != 0);

    private void OnNativeEditorAction(nint userData, uint action, int floor, double value) =>
        EditorActionRequested?.Invoke(new NativeEditorActionRequest((NativeEditorAction)action, floor, value));

    public void Dispose()
    {
        ShutdownDiagnostics.Begin("NativeRendererSession.Dispose");
        nint renderer = _renderer;
        if (renderer == nint.Zero)
        {
            ShutdownDiagnostics.Complete("NativeRendererSession.Dispose", "renderer=null");
            return;
        }

        NativeRendererNative.SetSelectionChangedCallback(renderer, null, nint.Zero);
        NativeRendererNative.SetFollowPlayerChangedCallback(renderer, null, nint.Zero);
        NativeRendererNative.SetEditorActionCallback(renderer, null, nint.Zero);
        _renderer = nint.Zero;
        ChildHwnd = nint.Zero;
        NativeRendererNative.Destroy(renderer);
        GC.SuppressFinalize(this);
        ShutdownDiagnostics.Complete("NativeRendererSession.Dispose");
    }
}
