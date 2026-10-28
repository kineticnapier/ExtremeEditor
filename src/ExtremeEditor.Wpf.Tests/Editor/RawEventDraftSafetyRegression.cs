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

        // A deleted event cannot contribute a draft to Save, even if malformed.
        LevelDocument deletionDocument = LevelDocument.CreateSynthetic(4);
        var deletionEditor = new EditorSession(deletionDocument);
        deletionEditor.AddAction(1, "SetSpeed");
        LevelAction action = deletionEditor.GetActionsAtFloor(1).Single();
        var deletionTab = new EditorDocumentTab(deletionDocument, deletionEditor);
        int sourceIndex = action.SourceIndex;
        deletionTab.PendingRawEventJson[sourceIndex] = "{invalid-json";
        deletionEditor.DeleteAction(action);

        bool orphanIsLive = MainWindow.TryResolveLiveRawEventDraft(
            deletionTab, sourceIndex, deletionTab.PendingRawEventJson[sourceIndex],
            out _, out _);
        Require(!orphanIsLive, "deleted event's invalid draft still blocks Save.");

        // Undo-before-save restores the event, so invalid JSON must still fail.
        deletionEditor.Undo();
        bool invalidLiveDraftRejected = false;
        try
        {
            MainWindow.TryResolveLiveRawEventDraft(
                deletionTab, sourceIndex, deletionTab.PendingRawEventJson[sourceIndex],
                out _, out _);
        }
        catch (JsonException)
        {
            invalidLiveDraftRejected = true;
        }
        Require(invalidLiveDraftRejected, "restored event's invalid draft must block Save.");

        // The restored action must still accept a valid draft.
        bool resolved = MainWindow.TryResolveLiveRawEventDraft(
            deletionTab, sourceIndex,
            "{\"floor\":1,\"eventType\":\"SetSpeed\"}",
            out LevelAction? original, out JsonObject? json);
        Require(resolved && original?.SourceIndex == sourceIndex &&
                json?["eventType"]?.GetValue<string>() == "SetSpeed",
            "restored event's valid draft must resolve.");

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
