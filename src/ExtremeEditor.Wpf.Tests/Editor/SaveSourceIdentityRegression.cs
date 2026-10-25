using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SaveSourceIdentityRegression
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-SaveSourceIdentity-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "level.adofai");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(path, CreateFixture().ToJsonString());
            var session = new EditorSession(AdoFaiLoader.Load(path).Document);
            LevelAction first = session.Document.ActionStore.Actions[0];
            session.DeleteAction(first);

            session.SaveAsync(path).GetAwaiter().GetResult();
            AssertRemainingPayload(path, "after first save");

            session.SaveAsync(path).GetAwaiter().GetResult();
            AssertRemainingPayload(path, "after repeated save");

            session.Undo();
            session.SaveAsync(path).GetAwaiter().GetResult();
            AssertPayloads(path, "removed-A", "remaining-B");

            session.Redo();
            session.SaveAsync(path).GetAwaiter().GetResult();
            AssertRemainingPayload(path, "after redo and save");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertRemainingPayload(string path, string phase)
    {
        JsonArray actions = ReadActions(path);
        if (actions.Count != 1 || actions[0] is not JsonObject remaining ||
            !string.Equals(remaining["futurePayload"]?.GetValue<string>(), "remaining-B", StringComparison.Ordinal) ||
            !string.Equals(remaining["trackColor"]?.GetValue<string>(), "00ff00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"RED: {phase} lost the surviving event's unknown properties because SourceIndex was stale.");
        }
    }

    private static void AssertPayloads(string path, params string[] expected)
    {
        JsonArray actions = ReadActions(path);
        string[] actual = actions
            .OfType<JsonObject>()
            .Select(action => action["futurePayload"]?.GetValue<string>() ?? "<missing>")
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Save/Undo did not preserve event identities. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    private static JsonArray ReadActions(string path) =>
        (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["actions"] as JsonArray
        ?? throw new InvalidOperationException("Saved fixture has no actions array.");

    private static JsonObject CreateFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 90, 180),
        ["settings"] = new JsonObject { ["bpm"] = 120 },
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "FutureCustomEvent",
                ["active"] = true,
                ["futurePayload"] = "removed-A",
                ["trackColor"] = "ff0000"
            },
            new JsonObject
            {
                ["floor"] = 2,
                ["eventType"] = "FutureCustomEvent",
                ["active"] = true,
                ["futurePayload"] = "remaining-B",
                ["trackColor"] = "00ff00"
            }
        },
        ["decorations"] = new JsonArray()
    };
}
