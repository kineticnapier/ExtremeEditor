using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SongSettingsAudioAsyncRegression
{
    internal static void Run()
    {
        string root = Environment.CurrentDirectory;
        string settingsSource = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.SongSettings.cs"));
        int methodStart = settingsSource.IndexOf(
            "private async Task RefreshAfterSongSettingsChangeAsync(", StringComparison.Ordinal);
        if (methodStart < 0)
            throw new InvalidOperationException("#87: song settings audio refresh is not asynchronous.");
        int methodEnd = settingsSource.IndexOf("\n    internal static ", methodStart, StringComparison.Ordinal);
        string method = settingsSource[methodStart..(methodEnd < 0 ? settingsSource.Length : methodEnd)];
        if (!method.Contains("await PreparedAudioLoad.PrepareAsync(", StringComparison.Ordinal))
            throw new InvalidOperationException("#87: song decoder and WaveOut preparation are not awaited off the UI thread.");
        if (method.Contains("LoadSong(", StringComparison.Ordinal) ||
            method.Contains("_audio.Unload()", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#87: song settings still unload or decode audio synchronously.");
        }

        string preparedSource = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Services", "PreparedAudioLoad.cs"));
        if (!preparedSource.Contains("internal static Task<PreparedAudioLoad> PrepareAsync(", StringComparison.Ordinal) ||
            !preparedSource.Contains("Task.Run(() => Prepare(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#87: PreparedAudioLoad has no background preparation entry point.");
        }

        if (!method.Contains("ReferenceEquals(_documentTabs.ActiveTab, targetTab)", StringComparison.Ordinal) ||
            !settingsSource.Contains("generation == targetTab.AudioPreparationGeneration", StringComparison.Ordinal) ||
            !method.Contains("prepared.Dispose();", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "#87: stale song preparation is not rejected for tab switches or consecutive edits.");
        }

        LevelDocument firstDocument = LevelDocument.CreateSynthetic(2);
        var firstTab = new EditorDocumentTab(firstDocument, new EditorSession(firstDocument));
        LevelDocument secondDocument = LevelDocument.CreateSynthetic(2);
        var secondTab = new EditorDocumentTab(secondDocument, new EditorSession(secondDocument));
        var tabs = new EditorDocumentTabs();
        tabs.Add(firstTab);
        tabs.Add(secondTab, activate: false);
        firstTab.AudioPreparationGeneration = 4;

        if (!MainWindow.CanApplyPreparedSongAudio(tabs, firstTab, firstDocument, generation: 4))
            throw new InvalidOperationException("#87: current preparation was rejected.");
        if (MainWindow.CanApplyPreparedSongAudio(tabs, firstTab, firstDocument, generation: 3))
            throw new InvalidOperationException("#87: superseded preparation was accepted.");

        tabs.Activate(secondTab);
        if (!MainWindow.CanApplyPreparedSongAudio(tabs, firstTab, firstDocument, generation: 4))
            throw new InvalidOperationException("#87: an inactive tab cannot retain its own prepared audio.");

        tabs.Remove(firstTab);
        if (MainWindow.CanApplyPreparedSongAudio(tabs, firstTab, firstDocument, generation: 4))
            throw new InvalidOperationException("#87: preparation for a closed tab was accepted.");
    }
}
