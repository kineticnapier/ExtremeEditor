using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

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
        Require(cycle.Length > 0 && cycle.Contains("CanSwitchDocumentTabs()", StringComparison.Ordinal) &&
                cycle.Contains("Count <= 1", StringComparison.Ordinal),
            "Cycling must be disabled during blocked switches or with fewer than two tabs.");
        Require(cycle.Contains("SelectNextDocumentTab(", StringComparison.Ordinal) &&
                cycle.Contains("_pendingTabNavigationTarget = next", StringComparison.Ordinal) &&
                cycle.Contains("ActivateDocumentTab(next, fromTabCycle: true)", StringComparison.Ordinal),
            "Repeated Ctrl+Tab must track its pending target and use the existing activation path.");

        string select = ExtractMethodBody(source, "internal static EditorDocumentTab? SelectNextDocumentTab(");
        Require(select.Contains("tabs.ActiveTab", StringComparison.Ordinal) &&
                select.Contains("tabs.Tabs.Contains(pendingTarget)", StringComparison.Ordinal) &&
                select.Contains("backwards", StringComparison.Ordinal) &&
                select.Contains("%", StringComparison.Ordinal),
            "Navigation must use a live pending target, fall back to the active tab and wrap.");

        string activate = ExtractMethodBody(source, "private async Task<bool> ActivateDocumentTab(");
        Require(activate.Contains("if (!fromTabCycle && _pendingTabNavigationTarget is not null)", StringComparison.Ordinal) &&
                activate.Contains("if (fromTabCycle)", StringComparison.Ordinal) &&
                activate.Contains("++_tabActivationGeneration", StringComparison.Ordinal) &&
                activate.Contains("_pendingTabNavigationTarget = null", StringComparison.Ordinal),
            "Direct tab clicks, cancellation and completed switches must clear pending navigation.");
        VerifyRapidNavigation();

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

    private static void VerifyRapidNavigation()
    {
        var tabs = new EditorDocumentTabs();
        EditorDocumentTab NewTab()
        {
            LevelDocument document = LevelDocument.CreateSynthetic(2);
            return new EditorDocumentTab(document, new EditorSession(document));
        }

        EditorDocumentTab a = NewTab();
        EditorDocumentTab b = NewTab();
        EditorDocumentTab c = NewTab();
        Require(tabs.Add(a) && tabs.Add(b, activate: false) &&
                tabs.Add(c, activate: false) && ReferenceEquals(tabs.ActiveTab, a),
            "Three tabs must start with A active.");

        EditorDocumentTab? first = MainWindow.SelectNextDocumentTab(tabs, null, backwards: false);
        EditorDocumentTab? second = MainWindow.SelectNextDocumentTab(tabs, first, backwards: false);
        Require(ReferenceEquals(first, b) && ReferenceEquals(second, c) &&
                ReferenceEquals(tabs.ActiveTab, a),
            "Two rapid forward requests during preparation must target C, not B twice.");

        first = MainWindow.SelectNextDocumentTab(tabs, null, backwards: true);
        second = MainWindow.SelectNextDocumentTab(tabs, first, backwards: true);
        Require(ReferenceEquals(first, c) && ReferenceEquals(second, b),
            "Two rapid reverse requests must target B.");

        Require(tabs.Remove(b), "The pending destination can be closed.");
        Require(ReferenceEquals(
                MainWindow.SelectNextDocumentTab(tabs, b, backwards: false), c),
            "A removed pending tab must fall back to the active tab.");
        Require(ReferenceEquals(
                MainWindow.SelectNextDocumentTab(tabs, c, backwards: false), a),
            "Two-tab forward cycling must return to the active tab and cancel pending activation.");
        Require(ReferenceEquals(
                MainWindow.SelectNextDocumentTab(tabs, c, backwards: true), a),
            "Two-tab reverse cycling must return to the active tab.");
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
