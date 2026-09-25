using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class LegacyEventRoundTripRegression
{
    private static readonly string[] ExpectedEventTypes =
    [
        "ChangeTrack",
        "FreeRoamWarning",
        "FutureCustomEvent"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> CustomProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ChangeTrack"] = ["trackColor", "legacyPayload"],
            ["FreeRoamWarning"] = ["position", "warningPayload"],
            ["FutureCustomEvent"] = ["customScalar", "customArray", "customObject"]
        };

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-LegacyRoundTrip-{Guid.NewGuid():N}");
        string sourcePath = Path.Combine(directory, "source.adofai");
        string savedPath = Path.Combine(directory, "saved.adofai");
        Directory.CreateDirectory(directory);

        try
        {
            JsonObject source = CreateFixture();
            File.WriteAllText(sourcePath, source.ToJsonString());

            LevelDocument document = AdoFaiLoader.Load(sourcePath).Document;
            LevelAction[] actions = document.ActionStore.Actions.ToArray();
            if (!actions.Select(action => action.EventType).SequenceEqual(ExpectedEventTypes, StringComparer.Ordinal))
                throw new InvalidOperationException("Legacy/internal/unknown event type strings changed during load.");
            if (actions.Any(action => action.Kind != LevelActionKind.Unknown))
                throw new InvalidOperationException("Legacy/internal/unknown events must use the existing Unknown action-kind fallback.");

            var session = new EditorSession(document);
            session.SaveAsync(savedPath).GetAwaiter().GetResult();

            JsonObject saved = JsonNode.Parse(File.ReadAllText(savedPath)) as JsonObject
                ?? throw new InvalidOperationException("The saved level root is not an object.");
            JsonArray sourceActions = source["actions"] as JsonArray
                ?? throw new InvalidOperationException("The source fixture has no actions.");
            JsonArray savedActions = saved["actions"] as JsonArray
                ?? throw new InvalidOperationException("The saved level has no actions.");

            foreach (string eventType in ExpectedEventTypes)
            {
                JsonObject expected = FindAction(sourceActions, eventType);
                JsonObject actual = FindAction(savedActions, eventType);
                if (!string.Equals(actual["eventType"]?.GetValue<string>(), eventType, StringComparison.Ordinal))
                    throw new InvalidOperationException($"{eventType} did not preserve its event type on save.");

                foreach (string property in CustomProperties[eventType])
                {
                    if (!JsonNode.DeepEquals(expected[property], actual[property]))
                        throw new InvalidOperationException($"{eventType} did not preserve custom property {property} on save.");
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonObject CreateFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 90, 180, 270),
        ["settings"] = new JsonObject
        {
            ["bpm"] = 100
        },
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "ChangeTrack",
                ["trackColor"] = "ff00ff",
                ["legacyPayload"] = new JsonObject
                {
                    ["mode"] = "Legacy",
                    ["values"] = new JsonArray(1, 2, 3)
                }
            },
            new JsonObject
            {
                ["floor"] = 2,
                ["eventType"] = "FreeRoamWarning",
                ["position"] = new JsonArray(12.5, -3.25),
                ["warningPayload"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["label"] = "keep-me"
                }
            },
            new JsonObject
            {
                ["floor"] = 3,
                ["eventType"] = "FutureCustomEvent",
                ["customScalar"] = 42.25,
                ["customArray"] = new JsonArray("alpha", false, 7),
                ["customObject"] = new JsonObject
                {
                    ["nested"] = "preserved"
                }
            }
        },
        ["decorations"] = new JsonArray()
    };

    private static JsonObject FindAction(JsonArray actions, string eventType) =>
        actions
            .OfType<JsonObject>()
            .Single(action => string.Equals(action["eventType"]?.GetValue<string>(), eventType, StringComparison.Ordinal));
}
