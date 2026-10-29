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
                cycle.Contains("RequestDocumentTabActivation(next, fromTabCycle: true)", StringComparison.Ordinal),
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
        // A fault while constructing the timing map or starting the preparation
        // task must be caught before changing the active document.
        int setupGuard = activate.IndexOf("try", StringComparison.Ordinal);
        int setupBuild = activate.IndexOf("WpfPlaybackSetupBuilder.Build(", StringComparison.Ordinal);
        int levelStart = activate.IndexOf("NativeLoadPreparationCache.StartLevel(", StringComparison.Ordinal);
        int playbackStart = activate.IndexOf("NativeLoadPreparationCache.StartPlayback(", StringComparison.Ordinal);
        int setupCatch = activate.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        int tabCommit = activate.IndexOf("_documentTabs.Activate(tab)", StringComparison.Ordinal);
        Require(setupGuard >= 0 && setupGuard < setupBuild &&
                setupBuild < levelStart && levelStart < playbackStart &&
                playbackStart < setupCatch && setupCatch < tabCommit &&
                activate.Contains("return false;", StringComparison.Ordinal),
            "Timing-map building and native preparation must share an exception boundary before activation.");
        int nativeUpload = activate.IndexOf("TryUploadDocumentTabScene(", StringComparison.Ordinal);
        int nativeCommit = activate.IndexOf("_documentTabs.Activate(tab)", StringComparison.Ordinal);
        int stopAudio = activate.IndexOf("beforeCommit: StopPlayback", StringComparison.Ordinal);
        Require(nativeUpload > setupCatch && nativeUpload < stopAudio &&
                stopAudio < nativeCommit &&
                activate.Contains("NativeViewport.SetLevelProfiled(previous.Document)", StringComparison.Ordinal) &&
                activate.Contains("NativeViewport.SetPlaybackTimelineProfiled(previousTiming)", StringComparison.Ordinal) &&
                !activate.Contains("        StopPlayback();", StringComparison.Ordinal),
            "A failed native upload must restore the previous scene before stopping audio or committing the tab.");
        VerifyNativeUploadRollback();
        VerifyRapidNavigation();
        VerifyActivationFailureObservation();

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

    private static void VerifyNativeUploadRollback()
    {
        var steps = new List<string>();
        bool ok = MainWindow.TryUploadDocumentTabScene(
            () => steps.Add("level"),
            () => steps.Add("playback"),
            () => steps.Add("rollback"),
            out Exception? uploadError,
            out Exception? restoreError,
            beforeCommit: () => steps.Add("stop"));
        Require(ok && uploadError is null && restoreError is null &&
                steps.SequenceEqual(new[] { "level", "playback", "stop" }),
            "Successful native uploads must stop playback before committing, without rollback.");

        steps.Clear();
        var levelFailure = new InvalidOperationException("injected level failure");
        ok = MainWindow.TryUploadDocumentTabScene(
            () => { steps.Add("level"); throw levelFailure; },
            () => steps.Add("playback"),
            () => steps.Add("rollback"),
            out uploadError, out restoreError);
        Require(!ok && ReferenceEquals(uploadError, levelFailure) &&
                restoreError is null &&
                steps.SequenceEqual(new[] { "level", "rollback" }),
            "Level upload failure must restore the old scene without committing playback.");

        steps.Clear();
        var playbackFailure = new InvalidOperationException("injected playback failure");
        ok = MainWindow.TryUploadDocumentTabScene(
            () => steps.Add("level"),
            () => { steps.Add("playback"); throw playbackFailure; },
            () => steps.Add("rollback"),
            out uploadError, out restoreError);
        Require(!ok && ReferenceEquals(uploadError, playbackFailure) &&
                restoreError is null &&
                steps.SequenceEqual(new[] { "level", "playback", "rollback" }),
            "Playback upload failure must roll back even after the level upload succeeds.");

        steps.Clear();
        var stopFailure = new InvalidOperationException("injected stop failure");
        ok = MainWindow.TryUploadDocumentTabScene(
            () => steps.Add("level"),
            () => steps.Add("playback"),
            () => steps.Add("rollback"),
            out uploadError, out restoreError,
            beforeCommit: () => { steps.Add("stop"); throw stopFailure; });
        Require(!ok && ReferenceEquals(uploadError, stopFailure) &&
                restoreError is null &&
                steps.SequenceEqual(new[] { "level", "playback", "stop", "rollback" }),
            "Stopping playback after upload must roll back the old native scene on failure.");

        steps.Clear();
        var recoveryFailure = new InvalidOperationException("injected recovery failure");
        ok = MainWindow.TryUploadDocumentTabScene(
            () => throw playbackFailure,
            null,
            () => throw recoveryFailure,
            out uploadError, out restoreError);
        Require(!ok && ReferenceEquals(uploadError, playbackFailure) &&
                ReferenceEquals(restoreError, recoveryFailure),
            "A failed rollback must preserve both the original and recovery errors.");

        steps.Clear();
        ok = MainWindow.TryUploadDocumentTabScene(
            () => steps.Add("level"), null,
            () => steps.Add("rollback"),
            out uploadError, out restoreError,
            beforeCommit: () => steps.Add("stop"));
        Require(ok && steps.SequenceEqual(new[] { "level", "stop" }),
            "Deferred playback uploads must stop old playback before committing.");
    }

    private static void VerifyActivationFailureObservation()
    {
        Exception? observed = null;
        int notifications = 0;
        void Report(Exception ex)
        {
            observed = ex;
            notifications++;
        }

        MainWindow.ObserveDocumentTabActivationAsync(
            () => Task.FromResult(true), Report).GetAwaiter().GetResult();
        MainWindow.ObserveDocumentTabActivationAsync(
            () => Task.FromResult(false), Report).GetAwaiter().GetResult();
        Require(observed is null && notifications == 0,
            "Successful or canceled tab activation must not report an exception.");

        var asyncFailure = new InvalidOperationException("injected asynchronous failure");
        MainWindow.ObserveDocumentTabActivationAsync(
            () => Task.FromException<bool>(asyncFailure), Report).GetAwaiter().GetResult();
        Require(ReferenceEquals(observed, asyncFailure) && notifications == 1,
            "A faulted activation task must be observed exactly once.");

        var syncFailure = new InvalidOperationException("injected synchronous failure");
        MainWindow.ObserveDocumentTabActivationAsync(
            () => throw syncFailure, Report).GetAwaiter().GetResult();
        Require(ReferenceEquals(observed, syncFailure) && notifications == 2,
            "A synchronous activation failure must also be observed exactly once.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string observer = ExtractMethodBody(source,
            "private async void RequestDocumentTabActivation(");
        Require(observer.Contains("await ObserveDocumentTabActivationAsync(", StringComparison.Ordinal) &&
                observer.Contains("Could not activate", StringComparison.Ordinal) &&
                observer.Contains("_pendingTabNavigationTarget = null", StringComparison.Ordinal),
            "WPF tab activation must display failures and clear stale Ctrl+Tab targets.");
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
