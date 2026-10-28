using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeSystemMenuRegression
{
    internal static void Run()
    {
        string bridge = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.NativeKeyboardBridge.cs");
        string keys = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.AdoFaiKeybinds.cs");

        // Alt+Space must be dispatched to Windows (SC_KEYMENU), not swallowed
        // by the WPF HwndSource hook or ComponentDispatcher preprocessor.
        if (bridge.Contains("WmSysCommand", StringComparison.Ordinal) ||
            bridge.Contains("IsAltSpaceSystemMenu", StringComparison.Ordinal) ||
            bridge.Contains("MainWindowMessageHook", StringComparison.Ordinal) ||
            bridge.Contains("ScKeyMenu", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "#92: the native keyboard bridge must not suppress Windows' Alt+Space system menu.");
        }

        // Keep the editor's ordinary Space-to-play shortcut guarded against Alt.
        if (!keys.Contains("if (!alt && !windows && !shift &&", StringComparison.Ordinal) ||
            !keys.Contains("(key == Key.P || key == Key.Space)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "#92: Alt+Space must not trigger the editor's Space-to-play shortcut.");
        }
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            string path = Path.Combine(
                current.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            current = current.Parent;
        }

        throw new InvalidOperationException("Source file not found: " + relativePath);
    }
}
