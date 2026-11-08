using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class IncrementalActionPropertyEditRegression
{
    internal static void Run()
    {
        VerifyVisualPropertyEditKeepsUnrelatedStorage();
        VerifySetSpeedRecomputesRatiosWithoutReplacingStorage();
    }

    private static void VerifyVisualPropertyEditKeepsUnrelatedStorage()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(100_000);
        var before = new LevelAction(10, "Flash", true, null, null, null, null)
        {
            SourceIndex = 1,
            PropertyOverrides = new JsonObject { ["startOpacity"] = 100 }
        };
        document.ReplaceActions([before]);
        document.ReplaceDecorations([
            new LevelDecoration(3, "AddDecoration")
            {
                SourceIndex = 2,
                Properties = new JsonObject()
            }
        ]);
        var session = new EditorSession(document);
        session.CopyFloors([before.Floor]);
        double[] angles = document.Angles;
        LevelActionStore actions = document.ActionStore;
        IReadOnlyList<LevelDecoration> decorations = document.Decorations;
        var after = before with
        {
            PropertyOverrides = new JsonObject { ["startOpacity"] = 50 }
        };

        session.ReplaceAction(before, after);
        AssertStorageIdentity(document, angles, actions, decorations, "edit");
        if (document.ActionStore.Actions[0].PropertyOverrides?["startOpacity"]?.GetValue<int>() != 50)
            throw new InvalidOperationException("#55: incremental edit did not update the action value.");

        session.Undo();
        AssertStorageIdentity(document, angles, actions, decorations, "undo");
        session.Redo();
        AssertStorageIdentity(document, angles, actions, decorations, "redo");
    }

    private static void VerifySetSpeedRecomputesRatiosWithoutReplacingStorage()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(8);
        var first = new LevelAction(1, "SetSpeed", true, "Bpm", 200, null, null)
        {
            SourceIndex = 1
        };
        var second = new LevelAction(2, "SetSpeed", true, "Bpm", 300, null, null)
        {
            SourceIndex = 2
        };
        document.ReplaceActions([first, second]);
        var session = new EditorSession(document);
        session.CopyFloors([first.Floor]);
        LevelActionStore actions = document.ActionStore;

        session.ReplaceAction(first, first with { BeatsPerMinute = 150 });

        if (!ReferenceEquals(actions, document.ActionStore))
            throw new InvalidOperationException("#55: SetSpeed property edit replaced the complete ActionStore.");
        if (Math.Abs(document.ActionStore.Actions[0].SpeedRatio!.Value - 1.5) > 1e-9 ||
            Math.Abs(document.ActionStore.Actions[1].SpeedRatio!.Value - 2.0) > 1e-9)
        {
            throw new InvalidOperationException("#55: incremental SetSpeed edit did not recompute downstream ratios.");
        }
    }

    private static void AssertStorageIdentity(
        LevelDocument document,
        double[] angles,
        LevelActionStore actions,
        IReadOnlyList<LevelDecoration> decorations,
        string operation)
    {
        if (!ReferenceEquals(angles, document.Angles))
            throw new InvalidOperationException($"#55: {operation} recopied every floor angle.");
        if (!ReferenceEquals(actions, document.ActionStore))
            throw new InvalidOperationException($"#55: {operation} rebuilt the complete ActionStore.");
        if (!ReferenceEquals(decorations, document.Decorations))
            throw new InvalidOperationException($"#55: {operation} recopied every decoration.");
    }
}
