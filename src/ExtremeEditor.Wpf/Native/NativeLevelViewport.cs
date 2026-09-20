using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ExtremeEditor.Wpf.Native;

internal sealed class NativeLevelViewport : HwndHost
{
    private NativeRendererSession? _session;

    public NativeLevelViewport()
    {
        SizeChanged += (_, _) => ResizeNativeChild();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        uint width = ToPixelExtent(ActualWidth);
        uint height = ToPixelExtent(ActualHeight);
        _session = NativeRendererSession.Create(hwndParent.Handle, width, height);
        return new HandleRef(this, _session.ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _session?.Dispose();
        _session = null;
    }

    private void ResizeNativeChild()
    {
        _session?.Resize(ToPixelExtent(ActualWidth), ToPixelExtent(ActualHeight));
    }

    private static uint ToPixelExtent(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 1u;

        return checked((uint)Math.Clamp(Math.Ceiling(value), 1d, uint.MaxValue));
    }
}
