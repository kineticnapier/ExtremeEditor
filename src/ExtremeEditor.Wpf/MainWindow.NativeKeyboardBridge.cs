using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const string NativeRendererWindowClass = "ExtremeEditor.NativeRenderer.Window";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ComponentDispatcher.ThreadPreprocessMessage += NativeThreadPreprocessMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= NativeThreadPreprocessMessage;
    }

    private void NativeThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled || NativeViewport.Visibility != Visibility.Visible)
            return;
        if (msg.message != WmKeyDown && msg.message != WmSysKeyDown)
            return;
        if (!IsNativeRendererWindow(msg.hwnd))
            return;

        Key key = KeyInterop.KeyFromVirtualKey(unchecked((int)msg.wParam));
        if (key == Key.None)
            return;

        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source is null)
            return;

        // HwndHost owns a real child HWND, so WPF's routed PreviewKeyDown is not
        // generated while that HWND has keyboard focus. Re-enter the exact same
        // ADOFAI key handler from the thread message pump instead of maintaining a
        // second key map in C++.
        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

        AdoFaiPreviewKeyDown(this, args);
        handled = args.Handled;
    }

    private static bool IsNativeRendererWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
            return false;

        var className = new StringBuilder(128);
        return GetClassName(hwnd, className, className.Capacity) > 0 &&
               string.Equals(className.ToString(), NativeRendererWindowClass, StringComparison.Ordinal);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);
}
