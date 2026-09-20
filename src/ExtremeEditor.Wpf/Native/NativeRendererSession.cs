using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

internal sealed class NativeRendererSession : IDisposable
{
    private const uint ExpectedApiVersion = 1;

    private nint _renderer;

    private NativeRendererSession(nint renderer, nint childHwnd)
    {
        _renderer = renderer;
        ChildHwnd = childHwnd;
    }

    internal nint ChildHwnd { get; private set; }
    internal int SelectedFloor => _renderer == nint.Zero ? -1 : NativeRendererNative.GetSelectedFloor(_renderer);

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

    internal void FrameAll()
    {
        if (_renderer != nint.Zero)
            NativeRendererNative.FrameAll(_renderer);
    }

    public void Dispose()
    {
        nint renderer = _renderer;
        if (renderer == nint.Zero)
            return;

        _renderer = nint.Zero;
        ChildHwnd = nint.Zero;
        NativeRendererNative.Destroy(renderer);
        GC.SuppressFinalize(this);
    }
}
