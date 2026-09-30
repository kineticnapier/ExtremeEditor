using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class DecorationBlendDiagnostics
{
    internal static void Log(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nonDefault = new List<Entry>();

        foreach (LevelDecoration decoration in level.Decorations)
        {
            if (!string.Equals(decoration.EventType, "AddDecoration", StringComparison.Ordinal))
                continue;

            JsonObject properties = decoration.Properties;
            string blendMode = ReadString(properties["blendMode"])?.Trim() ?? "None";
            if (blendMode.Length == 0)
                blendMode = "None";

            counts.TryGetValue(blendMode, out int count);
            counts[blendMode] = count + 1;

            if (string.Equals(blendMode, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            nonDefault.Add(new Entry(
                decoration.SourceIndex,
                ReadString(properties["decorationImage"]) ?? "<missing>",
                blendMode,
                ReadInt(properties["depth"], 0),
                ReadString(properties["maskingType"]) ?? "None"));
        }

        Console.WriteLine(
            "[decoration-blend-summary] " +
            (counts.Count == 0
                ? "<none>"
                : string.Join(' ', counts.Select(static pair => $"{pair.Key}={pair.Value}"))));

        foreach (Entry entry in nonDefault.OrderBy(static entry => entry.SourceIndex))
        {
            Console.WriteLine(
                $"[decoration-blend] sourceIndex={entry.SourceIndex} image={entry.Image} " +
                $"blendMode={entry.BlendMode} depth={entry.Depth} maskingType={entry.MaskingType}");
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return node?.ToString();
    }

    private static int ReadInt(JsonNode? node, int defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int result))
                return result;
            if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;
        }
        return defaultValue;
    }

    private readonly record struct Entry(
        int SourceIndex,
        string Image,
        string BlendMode,
        int Depth,
        string MaskingType);
}
