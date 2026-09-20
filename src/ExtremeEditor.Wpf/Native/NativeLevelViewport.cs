using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public sealed class NativeLevelViewport : HwndHost
{
    private NativeRendererSession? _session;
    private LevelDocument? _level;
    private NativeLevelSnapshot? _snapshot;
    private bool _frameAllPending;

    public NativeLevelViewport()
    {
        Focusable = false;
        SizeChanged += (_, _) => ResizeNativeChild();
    }

    public void SetLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        _level = level;
        _snapshot = null;
        UploadPendingLevel();
    }

    public void FrameAll()
    {
        if (_session is null)
        {
            _frameAllPending = true;
            return;
        }

        _session.FrameAll();
        _frameAllPending = false;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        uint width = ToPixelExtent(ActualWidth);
        uint height = ToPixelExtent(ActualHeight);
        _session = NativeRendererSession.Create(hwndParent.Handle, width, height);
        UploadPendingLevel();
        if (_frameAllPending)
        {
            _session.FrameAll();
            _frameAllPending = false;
        }
        return new HandleRef(this, _session.ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _session?.Dispose();
        _session = null;
    }

    private void UploadPendingLevel()
    {
        if (_session is null || _level is null)
            return;

        _snapshot ??= NativeLevelSnapshotBuilder.Build(_level);
        _session.SetLevel(_snapshot);
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
