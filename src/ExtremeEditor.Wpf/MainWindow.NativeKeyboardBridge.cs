using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private const int WmKillFocus = 0x0008;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const string NativeRendererWindowClass = "ExtremeEditor.NativeRenderer.Window";

    private readonly HashSet<Key> _nativeKeysDown = [];

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ComponentDispatcher.ThreadPreprocessMessage += NativeThreadPreprocessMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= NativeThreadPreprocessMessage;
    }

    private void NativeThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (NativeViewport.Visibility != Visibility.Visible || !IsNativeRendererWindow(msg.hwnd))
            return;

        if (msg.message == WmKillFocus)
        {
            _nativeKeysDown.Clear();
            return;
        }

        if (msg.message == WmKeyUp || msg.message == WmSysKeyUp)
        {
            Key released = KeyInterop.KeyFromVirtualKey(unchecked((int)msg.wParam));
            if (released != Key.None)
                _nativeKeysDown.Remove(released);
            return;
        }

        if (handled || (msg.message != WmKeyDown && msg.message != WmSysKeyDown))
            return;

        Key key = KeyInterop.KeyFromVirtualKey(unchecked((int)msg.wParam));
        if (key == Key.None)
            return;

        // ADOFAI's editor actions fire once on the physical key-down edge. Keep an
        // explicit down-set instead of trusting WM_KEYDOWN repeat metadata: it also
        // covers keyboard drivers/IME paths that synthesize repeated down messages.
        long keyFlags = msg.lParam.ToInt64();
        bool win32Repeat = (keyFlags & (1L << 30)) != 0;
        if (win32Repeat || !_nativeKeysDown.Add(key))
        {
            handled = true;
            return;
        }

        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source is null)
            return;

        // HwndHost owns a real child HWND, so WPF's routed PreviewKeyDown is not
        // reliably generated while that HWND has keyboard focus. Re-enter the exact
        // same ADOFAI key handler from the thread message pump instead of maintaining
        // a second key map in C++.
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