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

    internal static NativeRendererSession Create(nint parentHwnd, uint width, uint height)
    {
        if (parentHwnd == nint.Zero)
            throw new ArgumentException("Parent HWND must not be zero.", nameof(parentHwnd));

        uint apiVersion = NativeRendererNative.GetApiVersion();
        if (apiVersion != ExpectedApiVersion)
            throw new InvalidOperationException(
                $"Native renderer API version mismatch. Expected {ExpectedApiVersion}, got {apiVersion}.");

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
