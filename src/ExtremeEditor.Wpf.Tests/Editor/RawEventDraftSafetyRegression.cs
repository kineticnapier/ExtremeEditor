using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class RawEventDraftSafetyRegression
{
    internal static void Run()
    {
        PropertyInfo drafts = typeof(EditorDocumentTab).GetProperty(
            "PendingRawEventJson",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: raw event JSON drafts are not stored with their document tab.");

        LevelDocument document = LevelDocument.CreateSynthetic(2);
        var tab = new EditorDocumentTab(document, new EditorSession(document));

        document.SourcePath = Path.Combine(Path.GetTempPath(), "extremeeditor-video-export-draft-test.adofai");
        Require(!MainWindow.RequiresSaveBeforeVideoExport(tab),
            "clean saved chart unexpectedly requires pre-export Save.");
        tab.PendingRawEventJson[7] = "{\"floor\":1,\"eventType\":\"Flash\",\"startOpacity\":42}";
        Require(!tab.Editor.IsDirty, "Raw JSON draft should not dirty EditorSession before Apply.");
        Require(MainWindow.RequiresSaveBeforeVideoExport(tab),
            "video export skips an unapplied Raw JSON draft.");
        tab.PendingRawEventJson.Clear();
        document.SourcePath = "<synthetic>";
        Require(MainWindow.RequiresSaveBeforeVideoExport(tab),
            "video export skips Save As for an untitled chart.");

        object value = drafts.GetValue(tab)
            ?? throw new InvalidOperationException("RED: raw event draft storage is null.");
        MethodInfo add = value.GetType().GetMethod("Add", [typeof(int), typeof(string)])
            ?? throw new InvalidOperationException("RED: raw event draft storage is not keyed by SourceIndex.");
        add.Invoke(value, [7, "{\"floor\":1,\"eventType\":\"Flash\"}"]);
        if (!tab.IsDirty)
            throw new InvalidOperationException("RED: pending raw JSON does not mark its tab dirty.");

        string editor = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.Editor.cs");
        string properties = ReadSource(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPropertyEditor.cs");
        Require(properties.Contains("EventEditorText.TextChanged +=", StringComparison.Ordinal),
            "raw JSON edits must be captured before selection changes overwrite the TextBox.");
        Require(editor.Contains("TryCommitPendingRawEventDrafts(saveTab)", StringComparison.Ordinal),
            "normal Save must validate and apply pending raw JSON before writing.");
        Require(editor.Contains("PendingRawEventJson", StringComparison.Ordinal),
            "event selection must restore a SourceIndex-specific raw JSON draft.");

        const string mixedRaw = "{\"floor\":1,\"eventType\":\"Flash\",\"startColor\":\"112233\"}";
        Require(MainWindow.TryUpdateRawEventDraftProperty(
                mixedRaw, "endOpacity", 42, out JsonObject? mixedDraft),
            "valid Raw JSON cannot merge a property-field edit.");
        Require(mixedDraft!["startColor"]?.GetValue<string>() == "112233" &&
                mixedDraft["endOpacity"]?.GetValue<int>() == 42,
            "mixed Raw JSON and property edits do not retain both changes.");
        Require(!MainWindow.TryUpdateRawEventDraftProperty("{", "endOpacity", 42, out _),
            "invalid Raw JSON is treated as safe for property-field overwrite.");

        LevelDocument deletionDocument = LevelDocument.CreateSynthetic(4);
        var deletionEditor = new EditorSession(deletionDocument);
        deletionEditor.AddAction(1, "SetSpeed");
        LevelAction action = deletionEditor.GetActionsAtFloor(1).Single();
        var deletionTab = new EditorDocumentTab(deletionDocument, deletionEditor);
        int sourceIndex = action.SourceIndex;
        deletionTab.PendingRawEventJson[sourceIndex] = "{invalid-json";
        deletionEditor.DeleteAction(action);
        Require(!MainWindow.TryResolveLiveRawEventDraft(
                deletionTab, sourceIndex, deletionTab.PendingRawEventJson[sourceIndex], out _, out _),
            "deleted event's invalid draft still blocks Save.");

        deletionEditor.Undo();
        bool invalidLiveDraftRejected = false;
        try
        {
            MainWindow.TryResolveLiveRawEventDraft(
                deletionTab, sourceIndex, deletionTab.PendingRawEventJson[sourceIndex], out _, out _);
        }
        catch (JsonException)
        {
            invalidLiveDraftRejected = true;
        }
        Require(invalidLiveDraftRejected, "restored event's invalid draft must block Save.");
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"RED setup failure: source not found: {relativePath}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
