using System.IO;
using System.Reflection;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeSceneReuseRegression
{
    internal static void Run()
    {
        string snapshots = ReadSource("src/ExtremeEditor.Wpf/Native/NativeLevelSnapshotBuilder.cs");
        Require(snapshots.Contains("SceneCacheKey", StringComparison.Ordinal) &&
                snapshots.Contains("Interlocked.Increment", StringComparison.Ordinal),
            "Each rebuilt snapshot needs a new cache identity so an edited tab cannot restore a stale native scene.");

        string viewport = ReadSource("src/ExtremeEditor.Wpf/Native/NativeLevelViewport.cs");
        string preparation = ReadSource("src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs");
        Require(viewport.Contains("SetPreparedLevelCore(level, prepared,", StringComparison.Ordinal) &&
                viewport.Contains("_session.SetLevel(snapshot, useCache: true)", StringComparison.Ordinal) &&
                preparation.Contains("_session.SetLevel(_snapshot);", StringComparison.Ordinal),
            "Only prepared level uploads should opt into native scene reuse; ordinary uploads must remain uncached.");

        // HwndHost is also created for the welcome screen, before a chart
        // exists. Timeline setters require a level and must not run there.
        // Keep the guard between scene setup and the first timeline upload.
        int hostStart = preparation.IndexOf(
            "protected override HandleRef BuildWindowCore(", StringComparison.Ordinal);
        Require(hostStart >= 0, "Native host initialization must remain present.");
        int hostEnd = preparation.IndexOf(
            "protected override void DestroyWindowCore(", hostStart, StringComparison.Ordinal);
        Require(hostEnd > hostStart, "Native host initialization must have a bounded body.");
        string host = preparation[hostStart..hostEnd];
        int levelSetup = host.IndexOf("LastLevelUploadMetrics = UploadPendingLevel();", StringComparison.Ordinal);
        int noChartGuard = host.IndexOf("if (_level is null)", StringComparison.Ordinal);
        int guardReturn = noChartGuard < 0 ? -1 : host.IndexOf(
            "return new HandleRef(this, _session.ChildHwnd);", noChartGuard, StringComparison.Ordinal);
        int firstTimeline = host.IndexOf("UploadPendingPlaybackTimeline();", StringComparison.Ordinal);
        Require(levelSetup >= 0 && levelSetup < noChartGuard &&
                noChartGuard < guardReturn && guardReturn < firstTimeline,
            "Welcome-screen host creation must skip native timeline uploads until a level is loaded.");

        // An ordinary scene rebuild can fail while resolving decorations or
        // background assets. Do not cache a partially populated snapshot:
        // otherwise a retry skips the missing preparation steps.
        int uploadStart = preparation.IndexOf(
            "private NativeLevelUploadMetrics UploadPendingLevel()", StringComparison.Ordinal);
        int uploadEnd = preparation.IndexOf(
            "private void UpdateDecorationPlayback(", uploadStart, StringComparison.Ordinal);
        Require(uploadStart >= 0 && uploadEnd > uploadStart,
            "The ordinary scene upload path must remain available for regression checks.");
        string upload = preparation[uploadStart..uploadEnd];
        int candidate = upload.IndexOf("NativeLevelSnapshot pendingSnapshot = result.Snapshot;",
            StringComparison.Ordinal);
        int decorations = upload.IndexOf("pendingSnapshot.DecorationPlayback = decorationData.PlaybackRuntime;",
            StringComparison.Ordinal);
        int background = upload.IndexOf("pendingSnapshot.BackgroundAssets = backgroundAssets;",
            StringComparison.Ordinal);
        int commit = upload.IndexOf("_snapshot = pendingSnapshot;", StringComparison.Ordinal);
        int nativeUpload = upload.IndexOf("_session.SetLevel(_snapshot);", StringComparison.Ordinal);
        Require(candidate >= 0 && candidate < decorations && decorations < background &&
                background < commit && commit < nativeUpload &&
                !upload.Contains("_snapshot = result.Snapshot;", StringComparison.Ordinal),
            "A rebuilt snapshot must be committed only after decorations and background assets are ready.");

        // When a native animated-decoration upload fails, the managed
        // playback runtime must forget its last timestamp. Otherwise a paused
        // frame at the same chart time is silently skipped on retry.
        var playbackRuntime = new NativeDecorationPlaybackRuntime([], []);
        FieldInfo? lastTime = typeof(NativeDecorationPlaybackRuntime).GetField(
            "_lastTime", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(lastTime is not null, "Animated decoration playback must track its last time.");
        lastTime!.SetValue(playbackRuntime, 12.5d);
        playbackRuntime.InvalidateLastTime();
        Require(lastTime.GetValue(playbackRuntime) is double value && double.IsNaN(value),
            "Invalidation must permit a same-time animation update after upload failure.");

        string decorationUpload = preparation[
            preparation.IndexOf("private void UpdateDecorationPlayback(", StringComparison.Ordinal)..
            preparation.IndexOf("private void UploadPendingPlaybackTimeline(", StringComparison.Ordinal)];
        Require(decorationUpload.Contains("playback.InvalidateLastTime();", StringComparison.Ordinal) &&
                decorationUpload.Contains("catch", StringComparison.Ordinal) &&
                decorationUpload.Contains("throw;", StringComparison.Ordinal),
            "The viewport must invalidate animated-decoration time and rethrow failed native uploads.");

        string bridge = ReadSource("src/ExtremeEditor.Wpf/Native/NativeRendererNative.cs");
        Require(bridge.Contains("TryActivateCachedLevel", StringComparison.Ordinal) &&
                bridge.Contains("RememberCurrentLevel", StringComparison.Ordinal),
            "The native ABI must expose a cache-hit activation path and an explicit cache-miss registration path.");

        string session = ReadSource("src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs");
        Require(session.Contains("TryActivateCachedLevel(_renderer, snapshot.SceneCacheKey)", StringComparison.Ordinal) &&
                session.Contains("RememberCurrentLevel(_renderer, snapshot.SceneCacheKey)", StringComparison.Ordinal) &&
                session.Contains("NativeRendererNative.SetLevel(", StringComparison.Ordinal),
            "Prepared scene activation must bypass native scene creation on a hit and fall back to a full upload on a miss.");
        Require(session.Contains("SetStaticDecorations(snapshot.StaticDecorations", StringComparison.Ordinal),
            "Scene reuse must still restore the active document's decorations and visual resources.");

        string exports = ReadSource("src/ExtremeEditor.NativeRenderer/src/exports.cpp");
        string header = ReadSource("src/ExtremeEditor.NativeRenderer/include/extreme_editor_renderer.h");
        Require(exports.Contains("ee_renderer_try_activate_cached_level(", StringComparison.Ordinal) &&
                exports.Contains("ee_renderer_remember_current_level(", StringComparison.Ordinal) &&
                header.Contains("ee_renderer_try_activate_cached_level(", StringComparison.Ordinal) &&
                header.Contains("ee_renderer_remember_current_level(", StringComparison.Ordinal),
            "The native exports and public header must agree on the scene reuse ABI.");

        string renderer = ReadSource("src/ExtremeEditor.NativeRenderer/src/renderer.h");
        Require(renderer.Contains("TryActivateCachedLevel(", StringComparison.Ordinal) &&
                renderer.Contains("RememberCurrentLevel(", StringComparison.Ordinal) &&
                renderer.Contains("std::shared_ptr<LevelScene> cached_scene_", StringComparison.Ordinal),
            "Native scene reuse must retain at most one inactive scene instead of an unbounded scene map.");

        string cache = ReadSource("src/ExtremeEditor.Wpf/Services/NativeLoadPreparationCache.cs");
        Require(cache.Contains("ByLevel.Remove(level)", StringComparison.Ordinal),
            "Edits must discard their existing prepared snapshot and therefore its native scene identity.");

        VerifyShutdownClearsNativeCaches();

        string tabs = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs");
        Require(tabs.Contains("NativeViewport.SetPreparedLevelProfiled(tab.Document,", StringComparison.Ordinal),
            "Tab reactivation must continue through the prepared viewport path.");
    }

    private static void VerifyShutdownClearsNativeCaches()
    {
        // HwndHost may already have destroyed its child before the window
        // invokes ShutdownRenderer. Even that no-session path must release
        // managed references to the old camera timeline and invalidate the
        // paused playback anchor for a possible later renderer creation.
        var viewport = new NativeLevelViewport();
        Type type = typeof(NativeLevelViewport);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo? sourceField = type.GetField("_cameraUploadedSource", flags);
        FieldInfo? heightField = type.GetField("_cameraUploadedHeight", flags);
        FieldInfo? sessionField = type.GetField("_cameraUploadSession", flags);
        FieldInfo? anchorField = type.GetField("_playbackAnchorGate", flags);
        Require(sourceField is not null && heightField is not null &&
                sessionField is not null && anchorField is not null,
            "Native shutdown cache fields must remain available.");

        var previousCamera = new NativeCameraEvent[2];
        sourceField!.SetValue(viewport, previousCamera);
        heightField!.SetValue(viewport, 720u);
        var gate = (NativePlaybackAnchorDispatchGate)anchorField!.GetValue(viewport)!;
        Require(gate.ShouldSend(1.0, 1.0, active: false, playing: false) &&
                !gate.ShouldSend(1.0, 1.0, active: false, playing: false),
            "A stopped anchor must be cached before shutdown.");

        viewport.ShutdownRenderer();

        Require(sourceField.GetValue(viewport) is null &&
                heightField.GetValue(viewport) is uint height && height == 0u &&
                sessionField!.GetValue(viewport) is null,
            "Shutdown without a live session must release camera cache references.");
        Require(gate.ShouldSend(1.0, 1.0, active: false, playing: false),
            "Shutdown must invalidate the previous renderer's playback anchor.");

        // Repeated shutdowns must be safe when the child HWND is already gone.
        viewport.ShutdownRenderer();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string path = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new InvalidOperationException("RED setup failure: source missing: " + relativePath);
    }
}
