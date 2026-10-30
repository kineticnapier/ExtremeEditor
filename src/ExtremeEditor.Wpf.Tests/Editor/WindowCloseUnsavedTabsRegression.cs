using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class WindowCloseUnsavedTabsRegression
{
    public static void Run()
    {
        string root = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml.cs"));
        string tabs = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.DocumentTabs.cs"));
        string editor = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.Editor.cs"));

        if (!editor.Contains("protected override void OnClosing", StringComparison.Ordinal) ||
            !window.Contains("ConfirmWindowCloseAsync", StringComparison.Ordinal) ||
            !tabs.Contains("UnsavedDocumentCloseCoordinator", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "RED: closing the window has no cancelable multi-tab unsaved-document workflow.");
        }

        VerifyCancelPreservesAllDirtyTabs();
        VerifySaveFailureAndNewEditCancelClose();
        VerifySuccessfulSaveAndDiscardAllowClose();
    }

    private static void VerifyCancelPreservesAllDirtyTabs()
    {
        EditorDocumentTab first = CreateDirtyTab();
        EditorDocumentTab second = CreateDirtyTab();
        var decisions = new Queue<UnsavedDocumentCloseChoice>(
            [UnsavedDocumentCloseChoice.Discard, UnsavedDocumentCloseChoice.Cancel]);
        bool confirmed = UnsavedDocumentCloseCoordinator.ConfirmAsync(
                [first, second],
                _ => decisions.Dequeue(),
                _ => Task.FromResult(true))
            .GetAwaiter().GetResult();

        if (confirmed || !first.IsDirty || !second.IsDirty)
            throw new InvalidOperationException("Cancel must retain every dirty tab and its edits.");
    }

    private static void VerifySaveFailureAndNewEditCancelClose()
    {
        EditorDocumentTab failed = CreateDirtyTab();
        bool failureConfirmed = UnsavedDocumentCloseCoordinator.ConfirmAsync(
                [failed],
                _ => UnsavedDocumentCloseChoice.Save,
                _ => Task.FromResult(false))
            .GetAwaiter().GetResult();
        if (failureConfirmed || !failed.IsDirty)
            throw new InvalidOperationException("Save failure must cancel window close without clearing edits.");

        EditorDocumentTab editedDuringSave = CreateDirtyTab();
        bool editedConfirmed = UnsavedDocumentCloseCoordinator.ConfirmAsync(
                [editedDuringSave],
                _ => UnsavedDocumentCloseChoice.Save,
                tab =>
                {
                    tab.Editor.InsertAngle(1, 135);
                    return Task.FromResult(true);
                })
            .GetAwaiter().GetResult();
        if (editedConfirmed || !editedDuringSave.IsDirty)
            throw new InvalidOperationException("A tab edited during close-save must keep the window open.");
    }

    private static void VerifySuccessfulSaveAndDiscardAllowClose()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-WindowClose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EditorDocumentTab saved = CreateDirtyTab();
            EditorDocumentTab discarded = CreateDirtyTab();
            var decisions = new Queue<UnsavedDocumentCloseChoice>(
                [UnsavedDocumentCloseChoice.Save, UnsavedDocumentCloseChoice.Discard]);
            bool confirmed = UnsavedDocumentCloseCoordinator.ConfirmAsync(
                    [saved, discarded],
                    _ => decisions.Dequeue(),
                    async tab =>
                    {
                        await tab.Editor.SaveAsync(Path.Combine(directory, "saved.adofai"));
                        return true;
                    })
                .GetAwaiter().GetResult();

            if (!confirmed || saved.IsDirty || !discarded.IsDirty)
                throw new InvalidOperationException("Successful save plus explicit discard should allow close.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static EditorDocumentTab CreateDirtyTab()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(3);
        var editor = new EditorSession(document);
        editor.InsertAngle(1, 45);
        return new EditorDocumentTab(document, editor);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "ExtremeEditor.sln")) ||
                Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
