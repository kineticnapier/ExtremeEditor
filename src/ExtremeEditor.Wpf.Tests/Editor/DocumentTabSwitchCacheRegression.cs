using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabSwitchCacheRegression
{
    internal static void Run()
    {
        string tabs = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs");
        string activate = ExtractMethodBody(tabs, "private async Task<bool> ActivateDocumentTab(");
        Require(activate.Contains("NativeLoadPreparationCache.StartLevel(tab.Document)", StringComparison.Ordinal) &&
                activate.Contains("NativeViewport.SetPreparedLevelProfiled(tab.Document,", StringComparison.Ordinal),
            "Reactivating an unchanged chart must reuse its prepared native level instead of rebuilding its snapshot.");
        Require(activate.Contains("NativeLoadPreparationCache.StartPlayback(tab.Document, targetTimingMap)", StringComparison.Ordinal) &&
                activate.Contains("NativeViewport.SetPreparedPlaybackTimelineProfiled(", StringComparison.Ordinal),
            "Reactivating a chart must reuse its prepared playback timelines instead of rebuilding them on the UI thread.");
        Require(!activate.Contains("NativeViewport.SetLevel(tab.Document)", StringComparison.Ordinal) &&
                !activate.Contains("NativeViewport.SetPlaybackTimeline(_timingMap)", StringComparison.Ordinal),
            "Tab activation must not fall back to the always-rebuild native level and playback paths.");
        Require(activate.Contains("await Task.WhenAll", StringComparison.Ordinal) &&
                activate.Contains("activationGeneration != _tabActivationGeneration", StringComparison.Ordinal) &&
                !activate.Contains("GetAwaiter().GetResult()", StringComparison.Ordinal),
            "#19: tab activation must await preparation without blocking the UI and reject stale requests.");

        string cache = ReadSource("src/ExtremeEditor.Wpf/Services/NativeLoadPreparationCache.cs");
        Require(cache.Contains("InvalidateLevel(LevelDocument level)", StringComparison.Ordinal) &&
                cache.Contains("ByLevel.Remove(level)", StringComparison.Ordinal),
            "Native prepared-level cache must support invalidation after document edits.");
        Require(cache.Contains("RemoveFailedLevel", StringComparison.Ordinal) &&
                cache.Contains("RemoveFailedPlayback", StringComparison.Ordinal),
            "#47: failed or cancelled native preparation tasks must be evicted for retry.");

        string editor = ReadSource("src/ExtremeEditor.Wpf/Editor/EditorSession.cs");
        string changed = ExtractMethodBody(editor, "private void OnChanged()");
        Require(changed.Contains("NativeLoadPreparationCache.InvalidateLevel(Document)", StringComparison.Ordinal),
            "Edits, Undo and Redo must invalidate stale prepared native levels before future tab activation.");

        VerifyFailedTasksAreRetried();
    }

    private static void VerifyFailedTasksAreRetried()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        TimingMap timing = TimingMapBuilder.Build(level);

        Task<PreparedNativeLevel> failedLevel = NativeLoadPreparationCache.StartLevel(
            level, static _ => throw new InvalidOperationException("injected level failure"));
        try { _ = failedLevel.GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) when (ex.Message == "injected level failure") { }
        Task<PreparedNativeLevel> retriedLevel = NativeLoadPreparationCache.StartLevel(
            level, static _ => default);
        _ = retriedLevel.GetAwaiter().GetResult();
        if (ReferenceEquals(failedLevel, retriedLevel))
            throw new InvalidOperationException("#47: failed level preparation was reused instead of retried.");

        Task<PreparedNativePlayback> failedPlayback = NativeLoadPreparationCache.StartPlayback(
            level, timing, static (_, _) => throw new InvalidOperationException("injected playback failure"));
        try { _ = failedPlayback.GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) when (ex.Message == "injected playback failure") { }
        Task<PreparedNativePlayback> retriedPlayback = NativeLoadPreparationCache.StartPlayback(
            level, timing, static (_, _) => default);
        _ = retriedPlayback.GetAwaiter().GetResult();
        if (ReferenceEquals(failedPlayback, retriedPlayback))
            throw new InvalidOperationException("#47: failed playback preparation was reused instead of retried.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("RED setup failure: source unavailable: " + relativePath);
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
