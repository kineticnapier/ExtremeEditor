using System.Diagnostics;
using System.Text;
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
    private static readonly JsonDocumentOptions TolerantJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static LoadResult Load(string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] bytes = File.ReadAllBytes(path);
        TimeSpan read = sw.Elapsed;

        sw.Restart();
        ReadOnlyMemory<byte> jsonBytes = AdoFaiJson.NormalizeToUtf8(bytes);
        using JsonDocument json = JsonDocument.Parse(jsonBytes, TolerantJsonOptions);

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
                JsonValueKind.String when double.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double parsed) => parsed,
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
                if (action.ValueKind != JsonValueKind.Object)
                    continue;

                string type = action.TryGetProperty("eventType", out JsonElement eventType)
                    ? eventType.ValueKind == JsonValueKind.String
                        ? eventType.GetString() ?? "<null>"
                        : eventType.ToString()
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

internal static class AdoFaiJson
{
    // ADOFAI files in the wild are not guaranteed to be pristine UTF-8 JSON.
    // In particular, UTF-8 BOM files are common enough that the loader must not
    // feed the BOM directly to Utf8JsonReader. Keep this compatibility layer in
    // one place so future loose-format cases can be added without infecting the
    // level model/parser.
    public static ReadOnlyMemory<byte> NormalizeToUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsMemory(3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetBytes(text);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            string text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetBytes(text);
        }

        return bytes;
    }
}

public static class AdoFaiSaver
{
    public static void SaveAngles(LevelDocument document, string outputPath)
    {
        if (document.SourcePath == "<synthetic>" || !File.Exists(document.SourcePath))
            throw new InvalidOperationException("Synthetic levels cannot be saved by this prototype.");

        byte[] bytes = File.ReadAllBytes(document.SourcePath);
        ReadOnlyMemory<byte> jsonBytes = AdoFaiJson.NormalizeToUtf8(bytes);
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            jsonBytes,
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
