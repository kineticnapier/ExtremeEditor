using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class LooseSourceRootRegression
{
    public static void Run()
    {
        var rejected = new List<string>();
        VerifyCase("CR", "\r", rejected);
        VerifyCase("LF", "\n", rejected);
        VerifyCase("CRLF", "\r\n", rejected);

        if (rejected.Count > 0)
        {
            throw new InvalidOperationException(
                "editor source-root parse rejected loose ADOFAI string controls: " +
                string.Join(", ", rejected) + ".");
        }
    }

    private static void VerifyCase(string name, string rawControl, List<string> rejected)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-LooseSourceRoot-{name}-{Guid.NewGuid():N}");
        string sourcePath = Path.Combine(directory, "source.adofai");
        string savedPath = Path.Combine(directory, "saved.adofai");
        Directory.CreateDirectory(directory);

        try
        {
            string expectedText = "before" + rawControl + "after";
            string fixture = """
            {
              "angleData": [0, 90],
              "settings": { "bpm": 120 },
              "actions": [
                {
                  "floor": 1,
                  "eventType": "SetSpeed",
                  "speedType": "Bpm",
                  "beatsPerMinute": 120,
                  "futureText": "PLACEHOLDER"
                }
              ],
              "decorations": [],
              "futureRoot": { "keep": true }
            }
            """.Replace("PLACEHOLDER", expectedText, StringComparison.Ordinal);
            File.WriteAllText(sourcePath, fixture);

            WpfLevelLoadResult loaded = WpfLevelLoader.Load(sourcePath);
            LevelAction action = loaded.Document.ActionStore.Actions.Single();
            var session = new EditorSession(loaded.Document);

            JsonObject editable;
            try
            {
                editable = AdoFaiEditorSaveService.BuildEditableActionJson(session, action);
            }
            catch (JsonException)
            {
                rejected.Add(name);
                return;
            }

            AssertText(editable, expectedText, name, "editable action");

            AdoFaiEditorSaveService.SaveAsync(session, savedPath).GetAwaiter().GetResult();
            JsonObject saved = JsonNode.Parse(File.ReadAllText(savedPath)) as JsonObject
                ?? throw new InvalidOperationException($"{name}: saved root is not strict JSON object.");
            if (saved["futureRoot"]?["keep"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"{name}: save lost an unknown root property.");
            if (saved["actions"] is not JsonArray actions || actions[0] is not JsonObject savedAction)
                throw new InvalidOperationException($"{name}: save lost the source action.");
            AssertText(savedAction, expectedText, name, "saved action");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertText(JsonObject source, string expected, string name, string phase)
    {
        string? actual = source["futureText"]?.GetValue<string>();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{name}: {phase} did not preserve the unknown string property.");
        }
    }
}
