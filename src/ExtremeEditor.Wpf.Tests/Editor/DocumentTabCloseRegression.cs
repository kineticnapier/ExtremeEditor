using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabCloseRegression
{
    internal static void Run()
    {
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string close = ExtractMethodBody(source, "private void CloseDocumentTab(");
        Require(close.Length > 0,
            "Each document tab needs a dedicated close operation.");

        int dirty = close.IndexOf("tab.IsDirty", StringComparison.Ordinal);
        int confirm = close.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        int cancel = close.IndexOf("MessageBoxResult.Cancel", StringComparison.Ordinal);
        int remove = close.IndexOf("_documentTabs.Remove(tab)", StringComparison.Ordinal);
        Require(dirty >= 0 && confirm > dirty && cancel > confirm && remove > cancel,
            "Unsaved changes must be confirmed before removing a tab.");
        Require(close.Contains("ActivateDocumentTab(", StringComparison.Ordinal) &&
                close.IndexOf("ActivateDocumentTab(", StringComparison.Ordinal) < remove,
            "Closing the active tab must switch to a neighbor before removal.");
        Require(close.Contains("CreateNewDocumentTab()", StringComparison.Ordinal),
            "Closing the final tab must leave a new editable document.");
        Require(close.Contains("tab.Audio?.Dispose()", StringComparison.Ordinal),
            "Closing a tab must dispose its own audio player.");

        string refresh = ExtractMethodBody(source, "private void RefreshActiveDocumentTab(");
        Require(refresh.Contains("closeButton.Click +=", StringComparison.Ordinal) &&
                refresh.Contains("CloseDocumentTab(tab)", StringComparison.Ordinal) &&
                refresh.Contains("button.Click += (_, _) => ActivateDocumentTab(tab)", StringComparison.Ordinal),
            "Close and activate must be separate clickable controls.");

        string keybinds = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.AdoFaiKeybinds.cs"));
        string keyHandler = ExtractMethodBody(keybinds, "private void AdoFaiPreviewKeyDown(");
        int closeShortcut = keyHandler.IndexOf("key == Key.W", StringComparison.Ordinal);
        int floorInput = keyHandler.IndexOf("GetAdoFaiDirectionForKey(key, backQuote: false)", StringComparison.Ordinal);
        Require(closeShortcut >= 0 && floorInput > closeShortcut &&
                keyHandler.Contains("control && !shift && !alt && !windows && key == Key.W", StringComparison.Ordinal) &&
                keyHandler.Contains("CloseDocumentTab(activeTab)", StringComparison.Ordinal) &&
                keyHandler.Contains("_documentTabs.ActiveTab is EditorDocumentTab activeTab", StringComparison.Ordinal),
            "Ctrl+W must close only the current document before the W floor-input binding.");
        Require(keyHandler.Contains("if (!e.IsRepeat)", StringComparison.Ordinal) &&
                keyHandler.Contains("e.Handled = true;", StringComparison.Ordinal),
            "Ctrl+W must be edge-triggered and handled by the shared WPF/native keyboard path.");

        string bridge = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.NativeKeyboardBridge.cs"));
        Require(bridge.Contains("AdoFaiPreviewKeyDown(this, args)", StringComparison.Ordinal),
            "The HWND keyboard bridge must forward Ctrl+W to the shared shortcut handler.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"RED setup failure: unable to locate {relativePath}.");
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
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }
        return string.Empty;
    }
}
