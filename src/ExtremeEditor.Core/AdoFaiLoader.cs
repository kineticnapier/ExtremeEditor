using System.Diagnostics;
using System.Text.Json;

namespace ExtremeEditor.Core;

public sealed record LoadMetrics(
    TimeSpan Read,
    TimeSpan Parse,
    TimeSpan BuildPath,
    long FileBytes);

public sealed record LoadResult(LevelDocument Document, LoadMetrics Metrics);

public static class AdoFaiLoader
{
    public static LoadResult Load(string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] bytes = File.ReadAllBytes(path);
        TimeSpan read = sw.Elapsed;

        sw.Restart();
        using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("angleData", out JsonElement angleData) ||
            angleData.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "This prototype currently requires angleData. Legacy pathData is not implemented.");
        }

        var angles = new double[angleData.GetArrayLength()];
        int ai = 0;
        foreach (JsonElement value in angleData.EnumerateArray())
        {
            angles[ai++] = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                _ => throw new InvalidDataException(
                    $"Unsupported angleData value at index {ai - 1}: {value.ValueKind}")
            };
        }

        var actionTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        int actionCount = 0;
        if (root.TryGetProperty("actions", out JsonElement actions) &&
            actions.ValueKind == JsonValueKind.Array)
        {
            actionCount = actions.GetArrayLength();
            foreach (JsonElement action in actions.EnumerateArray())
            {
                string type = action.TryGetProperty("eventType", out JsonElement eventType)
                    ? eventType.GetString() ?? "<null>"
                    : "<unknown>";
                actionTypes.TryGetValue(type, out int count);
                actionTypes[type] = count + 1;
            }
        }

        TimeSpan parse = sw.Elapsed;

        sw.Restart();
        var positions = PathBuilder.BuildPositions(angles);
        var bounds = PathBuilder.CalculateBounds(positions);
        TimeSpan build = sw.Elapsed;

        var document = new LevelDocument
        {
            SourcePath = path,
            Angles = angles,
            Positions = positions,
            ActionCount = actionCount,
            ActionTypeCounts = actionTypes,
            Bounds = bounds
        };

        return new LoadResult(
            document,
            new LoadMetrics(read, parse, build, bytes.LongLength));
    }
}

public static class AdoFaiSaver
{
    public static void SaveAngles(LevelDocument document, string outputPath)
    {
        if (document.SourcePath == "<synthetic>" || !File.Exists(document.SourcePath))
            throw new InvalidOperationException("Synthetic levels cannot be saved by this prototype.");

        byte[] bytes = File.ReadAllBytes(document.SourcePath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            bytes,
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Root JSON value is not an object.");

        var angleData = new System.Text.Json.Nodes.JsonArray();
        foreach (double angle in document.Angles)
            angleData.Add(angle);
        node["angleData"] = angleData;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(outputPath, node.ToJsonString(options));
    }
}
