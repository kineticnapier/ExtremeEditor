using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabStateSafetyRegression
{
    internal static void Run()
    {
        VerifyPendingNavigationTarget();
        VerifyPendingSettingsCommit();
        VerifyNativeUploadAtomicity();
        VerifyUndoRefreshesEverySettingsPane();
    }

    private static void VerifyNativeUploadAtomicity()
    {
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string activate = ExtractMethodBody(source, "private async Task<bool> ActivateDocumentTab(");
        int upload = activate.IndexOf("NativeViewport.SetPreparedLevelProfiled(tab.Document", StringComparison.Ordinal);
        int commit = activate.IndexOf("_documentTabs.Activate(tab)", StringComparison.Ordinal);
        Require(upload >= 0 && commit > upload,
            "native level/timeline upload must succeed before committing active managed tab state");
        Require(activate.Contains("TryUploadDocumentTabScene(", StringComparison.Ordinal) &&
                activate.Contains("restorePrevious:", StringComparison.Ordinal) &&
                activate.Contains("Renderer recovery also failed", StringComparison.Ordinal),
            "native upload failures must be observed and restore the previous renderer state");
    }

    private static void VerifyPendingNavigationTarget()
    {
        var tabs = new EditorDocumentTabs();
        EditorDocumentTab a = CreateTab();
        EditorDocumentTab b = CreateTab();
        EditorDocumentTab c = CreateTab();
        Require(tabs.Add(a), "first tab must be active");
        Require(tabs.Add(b, activate: false) && tabs.Add(c, activate: false),
            "additional tabs must be registered");

        EditorDocumentTab? first = tabs.ResolveCycleTarget(null, backwards: false);
        EditorDocumentTab? second = tabs.ResolveCycleTarget(first, backwards: false);
        Require(ReferenceEquals(first, b) && ReferenceEquals(second, c),
            "two forward requests during preparation must advance from A through B to C");
        EditorDocumentTab? reverse = tabs.ResolveCycleTarget(second, backwards: true);
        Require(ReferenceEquals(reverse, b),
            "reverse navigation must use the most recently requested target");
    }

    private static void VerifyPendingSettingsCommit()
    {
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string activate = ExtractMethodBody(source, "private async Task<bool> ActivateDocumentTab(");
        int commit = activate.IndexOf("TryCommitSettingsBeforeDocumentChange()", StringComparison.Ordinal);
        int generation = activate.IndexOf("++_tabActivationGeneration", StringComparison.Ordinal);
        Require(commit >= 0 && generation > commit,
            "tab activation must commit or reject pending settings before asynchronous preparation");

        string close = ExtractMethodBody(source, "private async void CloseDocumentTab(");
        Require(close.IndexOf("TryCommitSettingsBeforeDocumentChange()", StringComparison.Ordinal) >= 0 &&
                close.IndexOf("TryCommitSettingsBeforeDocumentChange()", StringComparison.Ordinal) <
                close.IndexOf("tab.IsDirty", StringComparison.Ordinal),
            "tab close must commit pending settings before deciding whether to prompt for unsaved edits");
    }

    private static void VerifyUndoRefreshesEverySettingsPane()
    {
        string interaction = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EditorInteraction.cs"));
        Require(interaction.Contains("RefreshInspector()", StringComparison.Ordinal),
            "general mutations, including Undo/Redo, must refresh the inspector");
        string song = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.SongSettings.cs"));
        foreach (string refresh in new[]
                 {
                     "RefreshCameraSettings();",
                     "RefreshTrackSettings();",
                     "RefreshLevelMetadataSettings();",
                     "RefreshMiscSettings();"
                 })
        {
            Require(song.Contains(refresh, StringComparison.Ordinal),
                $"inspector refresh must include {refresh}");
        }
    }

    private static EditorDocumentTab CreateTab()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(2);
        return new EditorDocumentTab(document, new EditorSession(document));
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
        int depth = 0;
        for (int i = open; i >= 0 && i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }
        return string.Empty;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
