using System.Windows.Input;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeKeyboardFocusRegression
{
    internal static void Run()
    {
        var tracker = new NativeKeyDownTracker();
        Require(tracker.TryPress(Key.D, win32Repeat: false),
            "first physical key-down must be accepted");
        Require(!tracker.TryPress(Key.D, win32Repeat: true),
            "OS auto-repeat must remain suppressed");
        tracker.FocusLost();
        Require(tracker.TryPress(Key.D, win32Repeat: false),
            "a new physical press after native focus loss must not be mistaken for repeat");
        tracker.Release(Key.D);
        Require(tracker.TryPress(Key.D, win32Repeat: false),
            "ordinary key-up must also release the tracked key");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
