using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class LazyLevelMetadataRegression
{
    internal static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-LazyMetadata-{Guid.NewGuid():N}.adofai");
        string savedPath = Path.ChangeExtension(path, ".saved.adofai");
        try
        {
            File.WriteAllText(path,
                "{\"angleData\":[0,90],\"settings\":{" +
                "\"song\":\"Title\",\"artist\":\"Artist\",\"author\":\"Mapper\"," +
                "\"levelDesc\":\"Description\",\"levelTags\":\"tag-a\"," +
                "\"artistLinks\":\"https://example.invalid\",\"futureSetting\":42}," +
                "\"actions\":[{\"floor\":1,\"eventType\":\"Flash\",\"futureAction\":true}]," +
                "\"decorations\":[]}");

            LevelDocument document = AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document;
            var session = new EditorSession(document);
            LevelMetadataSettingsSnapshot metadata = session.GetLevelMetadataSettings();
            if (metadata != new LevelMetadataSettingsSnapshot(
                    "Title", "Artist", "Mapper", "Description", "tag-a", "https://example.invalid"))
            {
                throw new InvalidOperationException("#89: flat loader did not retain display metadata.");
            }

            FieldInfo sourceRoot = typeof(EditorSession).GetField(
                "_sourceRoot", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EditorSession source-root field is missing.");
            if (sourceRoot.GetValue(session) is not null)
            {
                throw new InvalidOperationException(
                    "#89: reading initial metadata synchronously parsed the complete source JSON tree.");
            }

            session.EditLevelMetadataSettings(metadata with { LevelDesc = "Edited" });
            if (sourceRoot.GetValue(session) is null)
                throw new InvalidOperationException("#89: metadata editing did not materialize the source tree.");
            session.SaveAsync(savedPath).GetAwaiter().GetResult();
            JsonObject saved = JsonNode.Parse(File.ReadAllText(savedPath)) as JsonObject
                ?? throw new InvalidOperationException("#89: saved metadata fixture is invalid JSON.");
            if (saved["settings"]?["futureSetting"]?.GetValue<int>() != 42 ||
                saved["settings"]?["levelDesc"]?.GetValue<string>() != "Edited" ||
                saved["actions"]?[0]?["futureAction"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("#89: lazy metadata edit/save lost unknown JSON or the edit.");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(savedPath))
                File.Delete(savedPath);
        }
    }
}
