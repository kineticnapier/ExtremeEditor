using System.IO;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

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
        Require(cycle.Contains("ResolveCycleTarget(", StringComparison.Ordinal) &&
                cycle.Contains("_pendingTabNavigationTarget", StringComparison.Ordinal) &&
                cycle.Contains("backwards", StringComparison.Ordinal),
            "Cycling must use creation order, move in either direction and wrap at both ends.");
        Require(cycle.Contains("RequestDocumentTabActivation(", StringComparison.Ordinal),
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

        // These checks previously existed but were never invoked by Run(),
        // so the standard WPF suite silently skipped native failure recovery.
        VerifyStopPlaybackNativeFailureOrdering();
        VerifyPreparedLevelStateIsAtomic();
        VerifyDirectPlaybackRefreshIsAtomic();
        VerifyPreparedPlaybackStateIsAtomic();
        VerifyNativeUploadRollback();
        VerifyActivationFailureObservation();
        VerifyNativeHostInitializationCleanup();
        VerifyNativeSessionConstructionCleanup();
        VerifyNativeSessionDisposalCleanup();
        VerifyRapidNavigation();
        VerifyFailedPendingTargetCleanup();
    }

    private static void VerifyStopPlaybackNativeFailureOrdering()
    {
        // The tab-switch transaction can roll back native uploads. A failed
        // ClearPlayback must leave the old tab's audio and silent clock intact.
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow.xaml.cs"));
        string stop = ExtractMethodBody(source, "private void StopPlayback()");
        int nativeClear = stop.IndexOf("NativeViewport.ClearPlayback();", StringComparison.Ordinal);
        int audioStop = stop.IndexOf("_audio.Stop();", StringComparison.Ordinal);
        int silentClear = stop.IndexOf("ClearSilentPlaybackState();", StringComparison.Ordinal);
        int uiUpdate = stop.IndexOf("PlayButton.Content = \"Play\";", StringComparison.Ordinal);
        Require(nativeClear >= 0 && nativeClear < audioStop &&
                audioStop < silentClear && silentClear < uiUpdate,
            "Native playback clearing must succeed before audio and UI state are stopped.");
    }

    private static void VerifyPreparedLevelStateIsAtomic()
    {
        // No native HWND is created: the injected upload simulates success
        // and a failure after the renderer has begun accepting the new scene.
        LevelDocument original = LevelDocument.CreateSynthetic(2);
        LevelDocument replacement = LevelDocument.CreateSynthetic(3);
        PreparedNativeLevel originalPrepared = NativeLevelViewport.PrepareLevel(original);
        PreparedNativeLevel replacementPrepared = NativeLevelViewport.PrepareLevel(replacement);
        var viewport = new NativeLevelViewport();

        viewport.SetPreparedLevelCore(original, originalPrepared, (_, _, _) => { });
        NativeLevelSnapshot? originalSnapshot = viewport.SnapshotForDiagnostics;
        Require(ReferenceEquals(originalSnapshot, originalPrepared.Snapshot),
            "The first successful upload must commit its managed snapshot.");

        var failure = new InvalidOperationException("injected native selection failure");
        Exception? caught = null;
        try
        {
            viewport.SetPreparedLevelCore(replacement, replacementPrepared,
                (_, _, _) => throw failure);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Require(ReferenceEquals(caught, failure) &&
                ReferenceEquals(viewport.SnapshotForDiagnostics, originalSnapshot),
            "A failed prepared-level upload must preserve the previous managed snapshot.");

        viewport.SetPreparedLevelCore(replacement, replacementPrepared, (_, _, _) => { });
        Require(ReferenceEquals(viewport.SnapshotForDiagnostics, replacementPrepared.Snapshot),
            "A later successful retry must commit the replacement snapshot.");
    }

    private static void VerifyDirectPlaybackRefreshIsAtomic()
    {
        // Regular editing and rollback can refresh timelines without going
        // through document-tab activation. That path must use the same
        // failure-safe preparation and upload as prepared tab switching.
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs"));
        string refresh = ExtractMethodBody(source,
            "public void SetPlaybackTimeline(TimingMap timingMap)");
        Require(refresh.Contains("PreparePlayback(level, timingMap)", StringComparison.Ordinal) &&
                refresh.Contains("NativePlaybackTimelineBuilder.Build(timingMap)", StringComparison.Ordinal) &&
                refresh.Contains("SetPreparedPlayback(prepared)", StringComparison.Ordinal) &&
                !refresh.Contains("_playbackTimeline =", StringComparison.Ordinal) &&
                !refresh.Contains("UploadPendingCameraTimeline()", StringComparison.Ordinal),
            "Direct playback refresh must prepare all timelines before committing and use atomic upload.");

        // VerifyPreparedPlaybackStateIsAtomic exercises the shared upload
        // failure path, including restoration of all eleven managed arrays.
    }

    private static void VerifyPreparedPlaybackStateIsAtomic()
    {
        // Simulate a native failure after any of the timeline uploads. The
        // managed timeline set and upload metrics must remain on the old scene.
        var viewport = new NativeLevelViewport();
        PreparedNativePlayback original = CreatePreparedPlayback(1, TimeSpan.FromMilliseconds(3));
        PreparedNativePlayback replacement = CreatePreparedPlayback(2, TimeSpan.FromMilliseconds(5));
        string[] fields =
        [
            "_playbackTimeline", "_cameraTimeline", "_flashTimeline",
            "_bloomTimeline", "_screenTileTimeline", "_filterTimeline",
            "_backgroundTimeline", "_trackTransformTimeline", "_trackVisualTimeline",
            "_trackAnimationTimeline", "_trackAnimationTimings"
        ];

        object ReadField(string name)
        {
            FieldInfo? field = typeof(NativeLevelViewport).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(viewport) ??
                throw new InvalidOperationException("RED: Missing timeline field " + name);
        }

        viewport.SetPreparedPlaybackCore(original, () => { });
        object[] previousFields = fields.Select(ReadField).ToArray();
        Require(viewport.LastPlaybackTimelineUploadMetrics.TimelineBuild == original.BuildTime,
            "Initial prepared playback upload must record its build metrics.");

        FieldInfo? cameraCache = typeof(NativeLevelViewport).GetField(
            "_cameraUploadedSource", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(cameraCache is not null, "Camera upload cache field must exist.");
        cameraCache!.SetValue(viewport, original.CameraTimeline);

        var failure = new InvalidOperationException("injected partial playback upload");
        Exception? caught = null;
        try
        {
            viewport.SetPreparedPlaybackCore(replacement, () => throw failure);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Require(ReferenceEquals(caught, failure),
            "A partial native playback upload failure must reach the caller.");
        for (int i = 0; i < fields.Length; ++i)
            Require(ReferenceEquals(ReadField(fields[i]), previousFields[i]),
                "Failed playback upload must preserve the previous " + fields[i] + ".");
        Require(cameraCache.GetValue(viewport) is null,
            "Failure must invalidate the camera upload cache to force native restoration.");
        Require(viewport.LastPlaybackTimelineUploadMetrics.TimelineBuild == original.BuildTime,
            "Failed playback upload must not overwrite successful upload metrics.");

        viewport.SetPreparedPlaybackCore(replacement, () => { });
        Require(ReferenceEquals(ReadField("_playbackTimeline"), replacement.PlaybackTimeline) &&
                ReferenceEquals(ReadField("_cameraTimeline"), replacement.CameraTimeline) &&
                viewport.LastPlaybackTimelineUploadMetrics.TimelineBuild == replacement.BuildTime,
            "A retry after failure must commit the replacement playback timelines.");
    }

    private static PreparedNativePlayback CreatePreparedPlayback(int count, TimeSpan buildTime)
        => new(
            new NativePlaybackTiming[count],
            new NativeCameraEvent[count],
            new NativeFlashEvent[count],
            new NativeBloomEvent[count],
            new NativeScreenTileEvent[count],
            new NativeFilterEvent[count],
            new NativeBackgroundEvent[count],
            new NativeTrackTransformEvent[count],
            new NativeTrackVisualEvent[count],
            new NativeTrackAnimationSegment[count],
            new NativeTrackAnimationTiming[count],
            buildTime);

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
        var selectionFailure = new InvalidOperationException("injected native selection failure");
        ok = MainWindow.TryUploadDocumentTabScene(
            () => steps.Add("level"),
            () => steps.Add("playback"),
            () => steps.Add("rollback"),
            out uploadError, out restoreError,
            beforeCommit: () => { steps.Add("selection"); throw selectionFailure; });
        Require(!ok && ReferenceEquals(uploadError, selectionFailure) &&
                restoreError is null &&
                steps.SequenceEqual(new[] { "level", "playback", "selection", "rollback" }),
            "A native selection upload failure must roll back before the managed tab commits.");

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
                observer.Contains("_pendingTabNavigationTarget = ClearFailedPendingTabTarget(", StringComparison.Ordinal),
            "WPF tab activation must display failures and clear stale Ctrl+Tab targets.");
    }

    private static void VerifyNativeHostInitializationCleanup()
    {
        var steps = new List<string>();
        int value = NativeLevelViewport.RunNativeHostInitialization(
            () => { steps.Add("initialize"); return 7; },
            () => steps.Add("cleanup"));
        Require(value == 7 && steps.SequenceEqual(new[] { "initialize" }),
            "Successful native host initialization must not dispose its renderer.");

        steps.Clear();
        var initializationFailure = new InvalidOperationException("injected native initialization failure");
        Exception? observed = null;
        try
        {
            NativeLevelViewport.RunNativeHostInitialization<int>(
                () => { steps.Add("initialize"); throw initializationFailure; },
                () => steps.Add("cleanup"));
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, initializationFailure) &&
                steps.SequenceEqual(new[] { "initialize", "cleanup" }),
            "A native initialization failure must dispose the partial renderer and rethrow.");

        steps.Clear();
        var cleanupFailure = new InvalidOperationException("injected native cleanup failure");
        observed = null;
        try
        {
            NativeLevelViewport.RunNativeHostInitialization<int>(
                () => { steps.Add("initialize"); throw initializationFailure; },
                () => { steps.Add("cleanup"); throw cleanupFailure; });
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, initializationFailure) &&
                steps.SequenceEqual(new[] { "initialize", "cleanup" }),
            "A cleanup failure must not hide the original native initialization error.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs"));
        string build = ExtractMethodBody(source,
            "protected override HandleRef BuildWindowCore(");
        string destroy = ExtractMethodBody(source,
            "protected override void DestroyWindowCore(");
        Require(build.Contains("RunNativeHostInitialization(", StringComparison.Ordinal) &&
                build.Contains("cleanup: () => DestroyWindowCore(default)", StringComparison.Ordinal) &&
                destroy.Contains("session.Dispose()", StringComparison.Ordinal) &&
                destroy.Contains("_cameraUploadSession = null", StringComparison.Ordinal),
            "HwndHost creation must invoke native cleanup and invalidate camera cache on failure.");
    }

    private static void VerifyNativeSessionConstructionCleanup()
    {
        // No native DLL or HWND is needed: simulate failures after the native
        // renderer has been created but before its managed session is returned.
        nint handle = (nint)17;
        var steps = new List<string>();
        int value = NativeRendererSession.CompleteCreation(
            handle,
            renderer =>
            {
                Require(renderer == handle, "The created renderer must reach session initialization.");
                steps.Add("initialize");
                return 7;
            },
            renderer =>
            {
                Require(renderer == handle, "Cleanup must receive the created renderer.");
                steps.Add("destroy");
            });
        Require(value == 7 && steps.SequenceEqual(new[] { "initialize" }),
            "Successful native session construction must retain its renderer.");

        steps.Clear();
        var constructionFailure = new InvalidOperationException("injected callback registration failure");
        Exception? observed = null;
        try
        {
            NativeRendererSession.CompleteCreation<int>(
                handle,
                _ => { steps.Add("initialize"); throw constructionFailure; },
                _ => steps.Add("destroy"));
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, constructionFailure) &&
                steps.SequenceEqual(new[] { "initialize", "destroy" }),
            "Failed native session construction must destroy the renderer exactly once.");

        steps.Clear();
        var cleanupFailure = new InvalidOperationException("injected native destroy failure");
        observed = null;
        try
        {
            NativeRendererSession.CompleteCreation<int>(
                handle,
                _ => { steps.Add("initialize"); throw constructionFailure; },
                _ => { steps.Add("destroy"); throw cleanupFailure; });
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, constructionFailure) &&
                steps.SequenceEqual(new[] { "initialize", "destroy" }),
            "Failed renderer cleanup must not hide the original construction error.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs"));
        string create = ExtractMethodBody(source,
            "internal static NativeRendererSession Create(");
        Require(create.Contains("return CompleteCreation(", StringComparison.Ordinal) &&
                create.Contains("NativeRendererNative.GetChildHwnd(handle)", StringComparison.Ordinal) &&
                create.Contains("new NativeRendererSession(handle, childHwnd)", StringComparison.Ordinal) &&
                create.Contains("NativeRendererNative.Destroy", StringComparison.Ordinal),
            "Session creation must guard child HWND lookup and callback registration with cleanup.");
    }

    private static void VerifyNativeSessionDisposalCleanup()
    {
        // Verify callback-detach failures still release the native renderer.
        var steps = new List<string>();
        NativeRendererSession.CompleteDisposal(
            () => steps.Add("detach"),
            () => steps.Add("destroy"));
        Require(steps.SequenceEqual(new[] { "detach", "destroy" }),
            "Normal disposal must detach callbacks before destroying the renderer.");

        var detachFailure = new InvalidOperationException("injected callback detach failure");
        var destroyFailure = new InvalidOperationException("injected native destroy failure");
        Exception? observed = null;
        steps.Clear();
        try
        {
            NativeRendererSession.CompleteDisposal(
                () => { steps.Add("detach"); throw detachFailure; },
                () => steps.Add("destroy"));
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, detachFailure) &&
                steps.SequenceEqual(new[] { "detach", "destroy" }),
            "Callback detach failure must still destroy the renderer and preserve the error.");

        observed = null;
        steps.Clear();
        try
        {
            NativeRendererSession.CompleteDisposal(
                () => steps.Add("detach"),
                () => { steps.Add("destroy"); throw destroyFailure; });
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, destroyFailure) &&
                steps.SequenceEqual(new[] { "detach", "destroy" }),
            "A native destroy failure must remain observable.");

        observed = null;
        steps.Clear();
        try
        {
            NativeRendererSession.CompleteDisposal(
                () => { steps.Add("detach"); throw detachFailure; },
                () => { steps.Add("destroy"); throw destroyFailure; });
        }
        catch (Exception ex)
        {
            observed = ex;
        }
        Require(ReferenceEquals(observed, detachFailure) &&
                steps.SequenceEqual(new[] { "detach", "destroy" }),
            "A second cleanup failure must not replace the original callback failure.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs"));
        string dispose = ExtractMethodBody(source, "public void Dispose()");
        Require(dispose.Contains("CompleteDisposal(", StringComparison.Ordinal) &&
                dispose.Contains("NativeRendererNative.Destroy(renderer)", StringComparison.Ordinal) &&
                dispose.IndexOf("_renderer = nint.Zero;", StringComparison.Ordinal) <
                dispose.IndexOf("CompleteDisposal(", StringComparison.Ordinal),
            "The real Dispose path must invalidate its handle before failure-safe cleanup.");
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

    private static void VerifyFailedPendingTargetCleanup()
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
                tabs.Add(c, activate: false), "Three tabs must be registered.");

        // A failed direct click on B must not make Ctrl+Tab skip B.
        EditorDocumentTab? pending = MainWindow.ClearFailedPendingTabTarget(b, b);
        Require(pending is null &&
                ReferenceEquals(MainWindow.SelectNextDocumentTab(
                    tabs, pending, backwards: false), b),
            "Failed direct activation must cycle from the still-active tab.");

        // A late failure from B must not erase a newer pending request for C.
        pending = MainWindow.ClearFailedPendingTabTarget(c, b);
        Require(ReferenceEquals(pending, c) &&
                ReferenceEquals(MainWindow.SelectNextDocumentTab(
                    tabs, pending, backwards: false), a),
            "Failure cleanup must preserve a newer pending destination.");

        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        string activation = ExtractMethodBody(source,
            "private async Task<bool> ActivateDocumentTab(");
        string observer = ExtractMethodBody(source,
            "private async void RequestDocumentTabActivation(");
        int cleanupCalls = activation.Split(
            "ClearFailedPendingTabTarget(", StringSplitOptions.None).Length - 1;
        Require(cleanupCalls >= 5 &&
                observer.Contains("ClearFailedPendingTabTarget(", StringComparison.Ordinal) &&
                !activation.Contains(
                    "fromTabCycle && ReferenceEquals(_pendingTabNavigationTarget, tab)",
                    StringComparison.Ordinal) &&
                !observer.Contains(
                    "fromTabCycle && ReferenceEquals(_pendingTabNavigationTarget, tab)",
                    StringComparison.Ordinal),
            "All failed direct and cyclic activations must clear only their own pending target.");
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
