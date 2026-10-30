using System.IO;
using System.Windows.Controls;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class UiSafetyIssueRegression
{
    internal static void Run()
    {
        var editorSurface = new Border();
        var viewportChild = new Button();
        editorSurface.Child = viewportChild;
        if (!EditorClipboardShortcutRouting.ShouldRouteEditorKeys(viewportChild, editorSurface))
            throw new InvalidOperationException("#28: editor-surface key input was rejected.");
        if (EditorClipboardShortcutRouting.ShouldRouteEditorKeys(new ListBoxItem(), editorSurface))
            throw new InvalidOperationException("#28: event-list focus can still trigger floor edit keys.");

        string tabs = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs");
        string refresh = Extract(tabs, "private void RefreshActiveDocumentTab()");
        if (refresh.Contains("Children.Clear", StringComparison.Ordinal) ||
            !tabs.Contains("_documentTabVisuals", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#25: tab status refresh still recreates every tab control.");
        }

        string properties = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPropertyEditor.cs");
        if (!properties.Contains("AutomationProperties.SetName(editor", StringComparison.Ordinal) ||
            !properties.Contains("$\"{property.Name} X\"", StringComparison.Ordinal) ||
            !properties.Contains("$\"{property.Name} Y\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#46: generated event property editors lack accessible names.");
        }

        string ci = Read(".github/workflows/ci.yml");
        if (!ci.Contains("cmake --build artifacts/native-ci", StringComparison.Ordinal) ||
            !ci.Contains("ctest --test-dir artifacts/native-ci", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#20: standard CI does not build and execute native tests.");
        }
    }

    private static string Read(string relative)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }

    private static string Extract(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int open = start < 0 ? -1 : source.IndexOf('{', start);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        return string.Empty;
    }
}
