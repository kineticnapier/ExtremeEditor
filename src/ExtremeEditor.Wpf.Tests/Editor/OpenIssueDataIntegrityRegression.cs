using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class OpenIssueDataIntegrityRegression
{
    internal static void Run()
    {
        VerifyUndoBranchDoesNotReuseSavedIdentity(); // #17
        VerifyPasteRedoRetainsOpaqueActionTemplate(); // #35
        VerifyPositionTrackDragRetainsSourceAttributes(); // #36
        VerifyInvalidCopyPreservesClipboard(); // #40
        VerifySameFloorActionOrderIsStable(); // #44
        VerifyClipboardIsSharedAcrossTabs(); // #48
    }

    private static void VerifyUndoBranchDoesNotReuseSavedIdentity()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(5);
        var editor = new EditorSession(document);
        editor.InsertAngle(1, 15);
        string path = Path.GetTempFileName();
        try
        {
            editor.SaveAsync(path).GetAwaiter().GetResult();
            editor.Undo();
            editor.InsertAngle(1, 25);
            if (!editor.IsDirty)
                throw new InvalidOperationException("#17: a divergent undo branch reused the saved revision identity.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyPasteRedoRetainsOpaqueActionTemplate()
    {
        WithFixture(root =>
        {
            var editor = new EditorSession(root.Document);
            editor.CopyFloors([2]);
            _ = editor.PasteFloors(4) ?? throw new InvalidOperationException("#35: fixture paste failed.");
            editor.Undo();
            editor.Redo();
            string saved = root.Path + ".saved";
            editor.SaveAsync(saved).GetAwaiter().GetResult();
            JsonObject pasted = LooseAdoFaiJson.ParseObject(saved)["actions"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(action => action["floor"]?.GetValue<int>() == 4);
            if (pasted["opaquePasteAttribute"]?.GetValue<string>() != "retain-after-redo")
                throw new InvalidOperationException("#35: paste undo/redo lost the copied action template.");
            File.Delete(saved);
        });
    }

    private static void VerifyPositionTrackDragRetainsSourceAttributes()
    {
        WithFixture(root =>
        {
            var editor = new EditorSession(root.Document);
            if (!editor.AddPositionTrackFromDrag(2, 0.5, -0.25))
                throw new InvalidOperationException("#36: source-backed PositionTrack drag was rejected.");
            string saved = root.Path + ".dragged";
            editor.SaveAsync(saved).GetAwaiter().GetResult();
            JsonObject action = LooseAdoFaiJson.ParseObject(saved)["actions"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(item => item["eventType"]?.GetValue<string>() == "PositionTrack");
            if (action["opaqueDragAttribute"]?.GetValue<string>() != "retain-after-drag" ||
                action["duration"]?.GetValue<double>() != 2.5)
            {
                throw new InvalidOperationException("#36: Alt-drag discarded existing PositionTrack attributes.");
            }
            File.Delete(saved);
        });
    }

    private static void VerifyInvalidCopyPreservesClipboard()
    {
        var editor = new EditorSession(LevelDocument.CreateSynthetic(7));
        editor.CopyFloors([2]);
        editor.CopyFloors([2, 4]);
        if (!editor.HasClipboard || editor.PasteFloors(5) is null)
            throw new InvalidOperationException("#40: an invalid copy erased the previous valid clipboard.");
    }

    private static void VerifySameFloorActionOrderIsStable()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-Issue44-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "level.adofai");
        try
        {
            var actions = new JsonArray();
            for (int i = 0; i < 40; i++)
            {
                actions.Add(new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = i == 0 ? "PositionTrack" : "CustomSameFloor",
                    ["orderToken"] = i,
                    ["positionOffset"] = i == 0 ? new JsonArray(0, 0) : null
                });
            }
            File.WriteAllText(path, CreateFixture(actions).ToJsonString());
            var editor = new EditorSession(AdoFaiLoader.Load(path).Document);
            if (!editor.AddPositionTrackFromDrag(2, 0.25, 0.5))
                throw new InvalidOperationException("#44: order fixture edit failed.");
            string saved = Path.Combine(directory, "saved.adofai");
            editor.SaveAsync(saved).GetAwaiter().GetResult();
            int[] order = LooseAdoFaiJson.ParseObject(saved)["actions"]!.AsArray()
                .Select(node => node!.AsObject()["orderToken"]!.GetValue<int>())
                .ToArray();
            if (!order.SequenceEqual(Enumerable.Range(0, 40)))
                throw new InvalidOperationException("#44: equal-floor action ordering changed during rebuild/save.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyClipboardIsSharedAcrossTabs()
    {
        var shared = new EditorClipboardStore();
        var first = new EditorSession(LevelDocument.CreateSynthetic(5), clipboardStore: shared);
        var second = new EditorSession(LevelDocument.CreateSynthetic(5), clipboardStore: shared);
        if (!first.CopyFloors([2]) || !second.HasClipboard || second.PasteFloors(3) is null)
            throw new InvalidOperationException("#48: copied floors are not available in another tab.");
        if (second.Document.FloorCount != 6)
            throw new InvalidOperationException("#48: cross-tab paste did not mutate only the destination chart.");
        if (first.Document.FloorCount != 5)
            throw new InvalidOperationException("#48: cross-tab paste mutated the source chart.");
    }

    private static void WithFixture(Action<(string Path, LevelDocument Document)> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-DataIntegrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "level.adofai");
        try
        {
            File.WriteAllText(path, CreateFixture(new JsonArray
            {
                new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = "PositionTrack",
                    ["positionOffset"] = new JsonArray(1.0, 2.0),
                    ["duration"] = 2.5,
                    ["opaquePasteAttribute"] = "retain-after-redo",
                    ["opaqueDragAttribute"] = "retain-after-drag"
                }
            }).ToJsonString());
            action((path, AdoFaiLoader.Load(path).Document));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonObject CreateFixture(JsonArray actions) => new()
    {
        ["angleData"] = new JsonArray(0, 45, 90, 135, 180, 225),
        ["settings"] = new JsonObject { ["bpm"] = 120 },
        ["actions"] = actions,
        ["decorations"] = new JsonArray()
    };
}
