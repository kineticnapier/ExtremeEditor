using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabNavigationRegression
{
    internal static void Run()
    {
        string keybinds = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.AdoFaiKeybinds.cs"));
        string handler = ExtractMethodBody(keybinds, "private void AdoFaiPreviewKeyDown(");
        int shortcut = handler.IndexOf("control && key == Key.Tab && !alt && !windows",
            StringComparison.Ordinal);
        int midspin = handler.IndexOf("if (!shift && key == Key.Tab)",
            StringComparison.Ordinal);
        Require(shortcut >= 0 && midspin > shortcut,
            "Ctrl+Tab must be routed before the ordinary Tab-to-Midspin binding.");
        Require(handler.Contains("CycleDocumentTab(backwards: shift)", StringComparison.Ordinal) &&
                handler.Contains("if (!e.IsRepeat)", StringComparison.Ordinal) &&
                handler.Contains("e.Handled = true;", StringComparison.Ordinal),
            "Ctrl+Tab and Ctrl+Shift+Tab must switch tabs only on a physical key-down edge.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string cycle = ExtractMethodBody(source, "private void CycleDocumentTab(bool backwards)");
        Require(cycle.Length > 0 && cycle.Contains("CanSwitchDocumentTabs()", StringComparison.Ordinal),
            "Cycling must be disabled during blocked document switches.");
        Require(cycle.Contains("_documentTabs.ActiveTab", StringComparison.Ordinal) &&
                cycle.Contains("_documentTabs.Tabs", StringComparison.Ordinal) &&
                cycle.Contains("backwards", StringComparison.Ordinal) &&
                cycle.Contains("%", StringComparison.Ordinal),
            "Cycling must use creation order, move in either direction and wrap at both ends.");
        Require(cycle.Contains("ActivateDocumentTab(", StringComparison.Ordinal),
            "Cycling must use the existing workspace-switch path, preserving audio and undo.");
        Require(cycle.Contains("Count <= 1", StringComparison.Ordinal),
            "Cycling with zero or one tab must be a no-op.");

        string bridge = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.NativeKeyboardBridge.cs"));
        // Native HWND input now passes through the shared event-palette dispatcher,
        // which delegates non-palette chords such as Ctrl+Tab to ADOFAI bindings.
        string palette = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventQuickPicker.cs"));
        Require(bridge.Contains(
                    "HandleExtremeEditorPreviewKeyDown(this, args, fromNativeChild: true)",
                    StringComparison.Ordinal) &&
                palette.Contains("AdoFaiPreviewKeyDown(sender, e)", StringComparison.Ordinal),
            "Child HWND key events must reach the shared Ctrl+Tab shortcut.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            $"RED setup failure: source not found: {relativePath}.");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        int open = source.IndexOf('{', start);
        if (open < 0)
            return string.Empty;

        int depth = 0;
        for (int i = open; i < source.Length; ++i)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }
        return string.Empty;
    }
}
