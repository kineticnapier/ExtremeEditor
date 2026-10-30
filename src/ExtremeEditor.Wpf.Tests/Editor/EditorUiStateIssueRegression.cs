using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorUiStateIssueRegression
{
    internal static void Run()
    {
        string main = Read("src/ExtremeEditor.Wpf/MainWindow.xaml.cs");
        string autoApply = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventAutoApply.cs");
        string properties = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPropertyEditor.cs");
        string tabs = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs");
        string tabState = Read("src/ExtremeEditor.Wpf/Editor/EditorDocumentTab.cs");
        string keys = Read("src/ExtremeEditor.Wpf/MainWindow/MainWindow.AdoFaiKeybinds.cs");
        string save = Read("src/ExtremeEditor.Wpf/Services/AdoFaiEditorSaveService.cs");
        string cache = Read("src/ExtremeEditor.Wpf/Services/NativeLoadPreparationCache.cs");

        Require(main.Contains("_playbackPreparationPending = false;", StringComparison.Ordinal) &&
                !main.Contains("if (!editorReady)\r\n                    _playbackPreparationPending", StringComparison.Ordinal),
            "#29 playback preparation pending state is not cleared on every completion path");
        Require(properties.Contains("_eventPropertyDraftBaseline", StringComparison.Ordinal) &&
                autoApply.Contains("JsonNode.DeepEquals(_eventPropertyDraftBaseline, draft)", StringComparison.Ordinal),
            "#31 untouched display defaults can still become an edit on focus loss");
        Require(!Extract(save, "private static JsonObject GetBaseActionObject").Contains(
                "GetSourceRootForSave", StringComparison.Ordinal),
            "#34 event selection still clones the complete source document");
        Require(autoApply.Contains("IsPlaybackVisualEvent", StringComparison.Ordinal) &&
                autoApply.Contains("\"Flash\" or \"Bloom\" or \"SetFilter\"", StringComparison.Ordinal),
            "#41 visual event commits do not refresh native playback timelines");
        Require(cache.Contains("PlaybackMapsByLevel", StringComparison.Ordinal) &&
                cache.Contains("PlaybackMapsByLevel.Remove(level)", StringComparison.Ordinal),
            "#39 editing a level does not invalidate its prepared playback timelines");
        Require(tabState.Contains("SelectedEventSourceIndex", StringComparison.Ordinal) &&
                tabs.Contains("previous.SelectedEventSourceIndex", StringComparison.Ordinal) &&
                tabs.Contains("tab.SelectedEventSourceIndex", StringComparison.Ordinal),
            "#43 event selection is not scoped to each document tab");
        Require(keys.Contains("ExecuteRouted(EditorCommands.Undo)", StringComparison.Ordinal) &&
                keys.Contains("ExecuteRouted(EditorCommands.Redo)", StringComparison.Ordinal),
            "#50 ADOFAI undo/redo shortcuts bypass the shared settings refresh path");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string Read(string relative)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Source unavailable: " + relative);
    }

    private static string Extract(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int open = start < 0 ? -1 : source.IndexOf('{', start);
        if (open < 0)
            return string.Empty;
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        return string.Empty;
    }
}
