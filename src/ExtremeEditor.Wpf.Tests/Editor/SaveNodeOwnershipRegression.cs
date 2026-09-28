using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SaveNodeOwnershipRegression
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-SaveNodeOwnership-{Guid.NewGuid():N}");
        string sourcePath = Path.Combine(directory, "source.adofai");
        string firstSavePath = Path.Combine(directory, "first-save.adofai");
        string secondSavePath = Path.Combine(directory, "second-save.adofai");
        Directory.CreateDirectory(directory);

        try
        {
            JsonObject fixture = CreateFixture();
            File.WriteAllText(sourcePath, fixture.ToJsonString());

            LevelDocument document = AdoFaiLoader.Load(sourcePath).Document;
            LevelAction sourceAction = document.ActionStore.Actions.Single() with { SourceIndex = 0 };

            // Model the edited action state that exposed the save-boundary ownership bug:
            // two logical actions can refer to the same source template, but the output
            // JsonArray must still receive two independently owned JsonObjects.
            document.ReplaceActions(
            [
                sourceAction,
                sourceAction with { Floor = 2, BeatsPerMinute = 150 }
            ]);

            var session = new EditorSession(document);
            LevelAction first = session.Document.ActionStore.Actions[0];
            session.ReplaceAction(first, first with { BeatsPerMinute = 175 });

            JsonObject sourceTree = session.GetSourceRootForSave();
            if (sourceTree["actions"] is not JsonArray sourceActions ||
                sourceActions[0] is not JsonObject parentedSourceAction ||
                !ReferenceEquals(parentedSourceAction.Parent, sourceActions))
            {
                throw new InvalidOperationException("Save ownership fixture did not contain a parented source action node.");
            }
            string sourceBeforeSave = sourceTree.ToJsonString();
            SaveExpectingIndependentNodes(session, firstSavePath);
            AssertSourceTreeUnchanged(session, sourceBeforeSave);
            AssertSavedActions(firstSavePath, expectedFirstBpm: 175);

            LevelAction editedAgain = session.Document.ActionStore.Actions[0];
            session.ReplaceAction(editedAgain, editedAgain with { BeatsPerMinute = 200 });
            SaveExpectingIndependentNodes(session, secondSavePath);
            AssertSourceTreeUnchanged(session, sourceBeforeSave);
            AssertSavedActions(secondSavePath, expectedFirstBpm: 200);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaveExpectingIndependentNodes(EditorSession session, string path)
    {
        try
        {
            AdoFaiEditorSaveService.SaveAsync(session, path).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("already has a parent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "saving edited actions reuses parented JsonNode.",
                ex);
        }
    }

    private static void AssertSourceTreeUnchanged(EditorSession session, string expected)
    {
        string actual = session.GetSourceRootForSave().ToJsonString();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Saving edited actions mutated the session source tree.");
    }

    private static void AssertSavedActions(string path, double expectedFirstBpm)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("Saved action ownership fixture is not a JSON object.");
        JsonArray actions = root["actions"] as JsonArray
            ?? throw new InvalidOperationException("Saved action ownership fixture has no actions array.");
        if (actions.Count != 2 || actions[0] is not JsonObject first || actions[1] is not JsonObject second)
            throw new InvalidOperationException("Saving edited actions lost a logical action.");

        double actualFirstBpm = first["beatsPerMinute"]?.GetValue<double>() ?? double.NaN;
        if (Math.Abs(actualFirstBpm - expectedFirstBpm) > 0.000001)
            throw new InvalidOperationException("Repeated save did not preserve the latest action edit.");

        JsonNode? expectedPayload = CreateUnknownPayload();
        if (!JsonNode.DeepEquals(first["futurePayload"], expectedPayload) ||
            !JsonNode.DeepEquals(second["futurePayload"], expectedPayload))
        {
            throw new InvalidOperationException("Saving edited actions lost unknown or nested action properties.");
        }
    }

    private static JsonObject CreateFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 90, 180),
        ["settings"] = new JsonObject
        {
            ["bpm"] = 120
        },
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "SetSpeed",
                ["speedType"] = "Bpm",
                ["beatsPerMinute"] = 120,
                ["futurePayload"] = CreateUnknownPayload()
            }
        },
        ["decorations"] = new JsonArray()
    };

    private static JsonObject CreateUnknownPayload() => new()
    {
        ["label"] = "keep-me",
        ["nested"] = new JsonArray(1, 2, new JsonObject { ["future"] = true })
    };
}
