using System.Runtime.InteropServices;

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
    private const uint ExpectedApiVersion = 6;

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
        if (_renderer == nint.Zero)
            return;

        NativeRendererNative.Resize(_renderer, Math.Max(1u, width), Math.Max(1u, height));
    }

    internal void SetLevel(NativeLevelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));
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
            if (pointsHandle.IsAllocated)
                pointsHandle.Free();
            if (geometriesHandle.IsAllocated)
                geometriesHandle.Free();
            if (floorsHandle.IsAllocated)
                floorsHandle.Free();
        }

        NativeRendererNative.ClearIconAssets(_renderer);
        foreach (NativeIconAsset asset in snapshot.IconAssets)
        {
            int result = NativeRendererNative.SetIconAsset(
                _renderer,
                asset.Id,
                asset.ImagePath,
                asset.OutlinePath);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Native icon asset upload failed for {asset.ImagePath} with result {result}.");
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

            NativeRendererNative.SetSelection(
                _renderer,
                pointer,
                checked((uint)floors.Length),
                primaryFloor);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    internal void SetPlaybackTimeline(NativePlaybackTiming[] timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));

        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (timings.Length > 0)
            {
                handle = GCHandle.Alloc(timings, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }

            int result = NativeRendererNative.SetPlaybackTimeline(
                _renderer,
                pointer,
                checked((uint)timings.Length));
            if (result != 0)
                throw new InvalidOperationException($"Native playback timeline upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    internal void SetCameraTimeline(NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));

        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (events.Length > 0)
            {
                handle = GCHandle.Alloc(events, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }

            int result = NativeRendererNative.SetCameraTimeline(
                _renderer,
                pointer,
                checked((uint)events.Length));
            if (result != 0)
                throw new InvalidOperationException($"Native camera timeline upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    internal void SetTrackTransformTimeline(NativeTrackTransformEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_renderer == nint.Zero)
            throw new ObjectDisposedException(nameof(NativeRendererSession));

        GCHandle handle = default;
        try
        {
            nint pointer = nint.Zero;
            if (events.Length > 0)
            {
                handle = GCHandle.Alloc(events, GCHandleType.Pinned);
                pointer = handle.AddrOfPinnedObject();
            }

            int result = NativeRendererNative.SetTrackTransformTimeline(
                _renderer,
                pointer,
                checked((uint)events.Length));
            if (result != 0)
                throw new InvalidOperationException($"Native track-transform timeline upload failed with result {result}.");
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    internal void SetPlaybackAnchor(double chartTime, double chartRate, bool active, bool playing)
    {
        if (_renderer == nint.Zero)
            return;

        uint flags = 0u;
        if (active)
            flags |= NativeRendererNative.PlaybackFlagActive;
        if (active && playing)
            flags |= NativeRendererNative.PlaybackFlagPlaying;

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

    private void OnNativeSelectionChanged(nint userData, int floor, uint modifiers)
    {
        SelectionChanged?.Invoke(floor, modifiers);
    }

    private void OnNativeFollowPlayerChanged(nint userData, int enabled)
    {
        FollowPlayerChanged?.Invoke(enabled != 0);
    }

    private void OnNativeEditorAction(nint userData, uint action, int floor, double value)
    {
        EditorActionRequested?.Invoke(new NativeEditorActionRequest((NativeEditorAction)action, floor, value));
    }

    public void Dispose()
    {
        nint renderer = _renderer;
        if (renderer == nint.Zero)
            return;

        NativeRendererNative.SetSelectionChangedCallback(renderer, null, nint.Zero);
        NativeRendererNative.SetFollowPlayerChangedCallback(renderer, null, nint.Zero);
        NativeRendererNative.SetEditorActionCallback(renderer, null, nint.Zero);
        _renderer = nint.Zero;
        ChildHwnd = nint.Zero;
        NativeRendererNative.Destroy(renderer);
        GC.SuppressFinalize(this);
    }
}
