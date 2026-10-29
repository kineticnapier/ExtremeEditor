using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabCloseRegression
{
    internal static void Run()
    {
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string close = ExtractMethodBody(source, "private async void CloseDocumentTab(");
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
        Require(close.Contains("await TryActivateReplacementAsync(", StringComparison.Ordinal) &&
                close.Contains("CreateNewDocumentTabAsync", StringComparison.Ordinal),
            "Closing the final tab must await successful replacement activation.");
        Require(close.Contains("_tabCloseOperationPending", StringComparison.Ordinal) &&
                close.Contains("finally", StringComparison.Ordinal),
            "Concurrent close requests must not create multiple replacement tabs.");
        Require(close.Contains("!await SaveAndCloseDocumentTabAsync(tab)", StringComparison.Ordinal) &&
                !close.Contains("_ = SaveAndCloseDocumentTabAsync(tab)", StringComparison.Ordinal),
            "Saving a dirty tab must be awaited under the same close-operation guard.");
        int closeGuardStart = close.IndexOf("_tabCloseOperationPending = true", StringComparison.Ordinal);
        int awaitedSave = close.IndexOf("await SaveAndCloseDocumentTabAsync(tab)", StringComparison.Ordinal);
        int closeGuardEnd = close.IndexOf("_tabCloseOperationPending = false", StringComparison.Ordinal);
        Require(closeGuardStart >= 0 && closeGuardStart < awaitedSave &&
                awaitedSave < closeGuardEnd,
            "The close guard must remain held until Save As finishes.");
        Require(close.Contains("catch (Exception ex)", StringComparison.Ordinal) &&
                close.Contains("Could not close", StringComparison.Ordinal),
            "An exception in async-void tab closing must be reported without crashing the dispatcher.");
        string saveAndClose = ExtractMethodBody(source,
            "private async Task<bool> SaveAndCloseDocumentTabAsync(");
        Require(saveAndClose.Contains("await ActivateDocumentTab(tab)", StringComparison.Ordinal) &&
                saveAndClose.Contains("await SaveCurrentAsync(saveAs: false)", StringComparison.Ordinal) &&
                saveAndClose.Contains("return saved && !tab.IsDirty", StringComparison.Ordinal) &&
                saveAndClose.Contains("_documentTabs.Tabs.Contains(tab)", StringComparison.Ordinal) &&
                !saveAndClose.Contains("CloseDocumentTab(tab)", StringComparison.Ordinal),
            "A canceled or failed Save As must keep the tab open without recursively closing it.");
        string createCommand = ExtractMethodBody(source,
            "private async void CreateNewDocumentTab(");
        Require(createCommand.Contains("await CreateNewDocumentTabAsync()", StringComparison.Ordinal) &&
                createCommand.Contains("catch (Exception ex)", StringComparison.Ordinal) &&
                createCommand.Contains("Could not create tab", StringComparison.Ordinal),
            "New-tab commands must observe exceptions rather than discard a faulted task.");
        string create = ExtractMethodBody(source,
            "private async Task<EditorDocumentTab?> CreateNewDocumentTabAsync(");
        Require(create.Contains("await ActivateDocumentTab(tab)", StringComparison.Ordinal) &&
                create.Contains("return null;", StringComparison.Ordinal) &&
                create.Contains("_documentTabs.Remove(tab)", StringComparison.Ordinal),
            "Failed or superseded new-tab activation must clean up its temporary tab.");
        int activateNewTab = create.IndexOf("await ActivateDocumentTab(tab)", StringComparison.Ordinal);
        int catchNewTab = create.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        int cleanupNewTab = create.IndexOf("_documentTabs.Remove(tab)", activateNewTab, StringComparison.Ordinal);
        Require(activateNewTab >= 0 && catchNewTab > activateNewTab &&
                cleanupNewTab > catchNewTab &&
                create.Contains("!ReferenceEquals(_documentTabs.ActiveTab, tab)", StringComparison.Ordinal) &&
                create.Contains("tab.Audio?.Dispose()", StringComparison.Ordinal) &&
                create.Contains("Could not activate new tab", StringComparison.Ordinal),
            "A failed new-tab activation must report the error and remove only inactive temporary tabs.");
        VerifyDelayedReplacement();
        Require(close.Contains("tab.Audio?.Dispose()", StringComparison.Ordinal),
            "Closing a tab must dispose its own audio player.");

        string refresh = ExtractMethodBody(source, "private void RefreshActiveDocumentTab(");
        Require(source.Contains("close.Click +=", StringComparison.Ordinal) &&
                source.Contains("CloseDocumentTab(tab)", StringComparison.Ordinal) &&
                source.Contains("activate.Click += (_, _) => _ = ActivateDocumentTab(tab)", StringComparison.Ordinal),
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
        // The native child now enters the common palette/keyboard dispatcher,
        // which delegates unhandled chords (including Ctrl+W) to ADOFAI bindings.
        // Checking only the former direct call rejects the correct routing path.
        string palette = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventQuickPicker.cs"));
        Require(bridge.Contains(
                    "HandleExtremeEditorPreviewKeyDown(this, args, fromNativeChild: true)",
                    StringComparison.Ordinal) &&
                palette.Contains("AdoFaiPreviewKeyDown(sender, e)", StringComparison.Ordinal),
            "The HWND keyboard bridge must forward Ctrl+W through the shared shortcut handler.");
    }

    private static void VerifyDelayedReplacement()
    {
        var tabs = new EditorDocumentTabs();
        LevelDocument firstDocument = LevelDocument.CreateSynthetic(2);
        var original = new EditorDocumentTab(
            firstDocument, new EditorSession(firstDocument));
        Require(tabs.Add(original), "initial chart must be registered");

        var pending = new TaskCompletionSource<EditorDocumentTab?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> replacementReady = MainWindow.TryActivateReplacementAsync(
            tabs, original, () => pending.Task);
        Require(!replacementReady.IsCompleted && tabs.Tabs.Count == 1,
            "closing the last tab must wait while native preparation is pending");

        LevelDocument secondDocument = LevelDocument.CreateSynthetic(2);
        var replacement = new EditorDocumentTab(
            secondDocument, new EditorSession(secondDocument));
        Require(tabs.Add(replacement, activate: false) &&
                ReferenceEquals(tabs.ActiveTab, original),
            "registering the replacement alone must not close the original");
        Require(!replacementReady.IsCompleted,
            "replacement must not be considered ready before activation");

        Require(tabs.Activate(replacement), "replacement can become active");
        pending.SetResult(replacement);
        Require(replacementReady.GetAwaiter().GetResult(),
            "successful delayed activation must allow final-tab removal");
        Require(tabs.Remove(original) && tabs.Tabs.Count == 1 &&
                ReferenceEquals(tabs.ActiveTab, replacement),
            "after activation only the replacement tab must remain");

        var failed = new TaskCompletionSource<EditorDocumentTab?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> failedActivation = MainWindow.TryActivateReplacementAsync(
            tabs, replacement, () => failed.Task);
        failed.SetResult(null);
        Require(!failedActivation.GetAwaiter().GetResult() &&
                tabs.Tabs.Count == 1 &&
                ReferenceEquals(tabs.ActiveTab, replacement),
            "failed activation must leave the existing tab intact");

        Task<bool> sameTab = MainWindow.TryActivateReplacementAsync(
            tabs, replacement, () => Task.FromResult<EditorDocumentTab?>(replacement));
        Require(!sameTab.GetAwaiter().GetResult(),
            "the closing tab cannot count as its own replacement");
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
