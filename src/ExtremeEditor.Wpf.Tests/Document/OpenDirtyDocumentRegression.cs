using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class OpenDirtyDocumentRegression
{
    public static void Run()
    {
        string source = File.ReadAllText(FindSourceFile("src/ExtremeEditor.Wpf/MainWindow.xaml.cs"));
        string open = ExtractMethodBody(source, "private async void ExecuteOpen(");
        string load = ExtractMethodBody(source, "private async Task LoadLevelAsync(");
        if (string.IsNullOrEmpty(open) || string.IsNullOrEmpty(load))
            throw new InvalidOperationException("RED setup failure: MainWindow open/load methods were not found.");

        Require(open.Contains("new OpenFileDialog", StringComparison.Ordinal),
            "Open still uses a file-selection dialog.");
        Require(!open.Contains("if (_documentTabs.Tabs.Count > 1)", StringComparison.Ordinal),
            "Open must not reject existing document tabs.");
        Require(!open.Contains("ConfirmDiscardCurrentChanges()", StringComparison.Ordinal),
            "Opening another chart must not ask to discard unsaved edits.");

        int dialog = open.IndexOf("dialog.ShowDialog(this)", StringComparison.Ordinal);
        int duplicate = open.IndexOf("_documentTabs.FindByPath(dialog.FileName)", StringComparison.Ordinal);
        int activation = open.IndexOf("ActivateDocumentTab(", StringComparison.Ordinal);
        int loading = open.IndexOf("LoadLevelAsync(dialog.FileName)", StringComparison.Ordinal);
        Require(dialog >= 0 && duplicate > dialog && activation > duplicate && loading > duplicate,
            "Opening the same path must select the already-open tab without loading it again.");

        Require(load.Contains("new EditorDocumentTab(", StringComparison.Ordinal) &&
                load.Contains("_documentTabs.Add(", StringComparison.Ordinal) &&
                load.Contains("ActivateDocumentTab(", StringComparison.Ordinal),
            "A newly loaded file must be added as a separate, active tab.");
        Require(!load.Contains("EnsureEditorSession();", StringComparison.Ordinal) &&
                !load.Contains("_audio.Unload();", StringComparison.Ordinal),
            "Loading another chart must not reset tab ownership or unload the previous tab's audio.");
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
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
