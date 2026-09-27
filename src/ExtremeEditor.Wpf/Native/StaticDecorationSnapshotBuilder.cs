using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct StaticDecorationSnapshotData(
    NativeStaticDecoration[] Instances,
    NativeDecorationAsset[] Assets);

internal static class StaticDecorationSnapshotBuilder
{
    private const float DegreesToRadians = MathF.PI / 180f;

    internal static StaticDecorationSnapshotData Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var instances = new List<NativeStaticDecoration>();
        var assets = new List<NativeDecorationAsset>();
        var assetIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string? levelDirectory = level.SourcePath == "<synthetic>"
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(level.SourcePath));

        foreach (LevelDecoration decoration in level.Decorations)
        {
            if (!string.Equals(decoration.EventType, "AddDecoration", StringComparison.Ordinal))
                continue;

            JsonObject properties = decoration.Properties;
            if (!ReadBool(properties["visible"], defaultValue: true))
                continue;

            float opacity = Math.Clamp(ReadFloat(properties["opacity"], 100f) / 100f, 0f, 1f);
            if (opacity <= 0f)
                continue;

            string? relativeTo = ReadString(properties["relativeTo"]);
            relativeTo = string.IsNullOrWhiteSpace(relativeTo) ? "Tile" : relativeTo.Trim();
            uint relativeMode;
            if (string.Equals(relativeTo, "Tile", StringComparison.OrdinalIgnoreCase))
            {
                relativeMode = NativeStaticDecoration.RelativeTile;
                if (decoration.Floor is not int floor || (uint)floor >= (uint)level.Positions.Length)
                    continue;
            }
            else if (string.Equals(relativeTo, "Global", StringComparison.OrdinalIgnoreCase))
            {
                relativeMode = NativeStaticDecoration.RelativeGlobal;
            }
            else
            {
                // Camera/CameraAspect/planets/LastPosition require runtime reference
                // state and intentionally remain outside the #14 static subset.
                continue;
            }

            string? image = ReadString(properties["decorationImage"]);
            if (string.IsNullOrWhiteSpace(image) || levelDirectory is null)
                continue;

            string imagePath;
            try
            {
                imagePath = Path.GetFullPath(Path.Combine(levelDirectory, image));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(imagePath))
                continue;

            if (!assetIds.TryGetValue(imagePath, out uint assetId))
            {
                assetId = checked((uint)assets.Count);
                assetIds.Add(imagePath, assetId);
                assets.Add(new NativeDecorationAsset(assetId, imagePath));
            }

            (float positionX, float positionY) = ReadPair(properties["position"], 0f, 0f);
            (float pivotX, float pivotY) = ReadPair(properties["pivotOffset"], 0f, 0f);
            (float scaleX, float scaleY) = ReadPair(properties["scale"], 100f, 100f);
            float rotationDegrees = ReadFloat(properties["rotation"], 0f);
            int depth = ReadInt(properties["depth"], 0);
            uint color = ReadColor(properties["color"]);

            instances.Add(new NativeStaticDecoration
            {
                SourceIndex = decoration.SourceIndex,
                Floor = decoration.Floor ?? -1,
                AssetId = assetId,
                RelativeMode = relativeMode,
                PositionX = positionX,
                PositionY = positionY,
                PivotOffsetX = pivotX,
                PivotOffsetY = pivotY,
                RotationRadians = rotationDegrees * DegreesToRadians,
                ScaleX = scaleX / 100f,
                ScaleY = scaleY / 100f,
                Color = color,
                Opacity = opacity,
                Depth = depth,
                Flags = NativeStaticDecoration.FlagVisible
            });
        }

        // Keep source order as the stable tie-break while preserving depth for the
        // native renderer. Do not pre-sort here: SourceIndex is also part of the
        // regression/debug contract.
        return new StaticDecorationSnapshotData(instances.ToArray(), assets.ToArray());
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return node?.ToString();
    }

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
            return defaultValue;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool result))
                return result;
            if (value.TryGetValue(out string? text) && bool.TryParse(text, out result))
                return result;
        }
        return defaultValue;
    }

    private static float ReadFloat(JsonNode? node, float defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out float single) && float.IsFinite(single))
                return single;
            if (value.TryGetValue(out double number) && double.IsFinite(number))
                return (float)number;
            if (float.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out single) &&
                float.IsFinite(single))
                return single;
        }
        return defaultValue;
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

    private static (float X, float Y) ReadPair(JsonNode? node, float defaultX, float defaultY)
    {
        if (node is not JsonArray array || array.Count < 2)
            return (defaultX, defaultY);
        return (ReadFloat(array[0], defaultX), ReadFloat(array[1], defaultY));
    }

    private static uint ReadColor(JsonNode? node)
    {
        string? text = ReadString(node)?.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(text))
            return 0x00FF_FFFFu;

        if (text.Length >= 6 &&
            uint.TryParse(text[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return rgb & 0x00FF_FFFFu;

        return 0x00FF_FFFFu;
    }
}
