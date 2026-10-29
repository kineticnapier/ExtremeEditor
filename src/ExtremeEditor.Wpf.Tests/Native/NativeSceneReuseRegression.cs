using System.IO;

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

        string tabs = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs");
        Require(tabs.Contains("NativeViewport.SetPreparedLevelProfiled(tab.Document,", StringComparison.Ordinal),
            "Tab reactivation must continue through the prepared viewport path.");
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
