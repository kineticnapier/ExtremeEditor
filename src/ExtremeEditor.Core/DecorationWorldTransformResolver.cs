using System.Numerics;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// Converts the chart-space decoration position produced by <see cref="DecorationState"/>
/// into the stock game's world-space placement for placement modes whose world transform
/// is independent of camera/planet/parallax state.
/// </summary>
public static class DecorationWorldTransformResolver
{
    public static Vector2 ResolveWorldPosition(
        LevelDocument level,
        LevelDecoration decoration,
        DecorationState state)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(state);

        var logicalWorldOffset = new Vector2(
            checked((float)(state.PositionX * PathBuilder.DefaultLongTileSize)),
            checked((float)(state.PositionY * PathBuilder.DefaultLongTileSize)));

        string placement = ReadString(decoration.Properties["relativeTo"]) ?? "Tile";
        if (string.Equals(placement, "Global", StringComparison.OrdinalIgnoreCase))
            return logicalWorldOffset;

        if (string.Equals(placement, "Tile", StringComparison.OrdinalIgnoreCase))
        {
            int floor = decoration.Floor ?? ReadInt32(decoration.Properties["floor"], 0);
            if ((uint)floor >= (uint)level.Positions.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(decoration),
                    floor,
                    "Decoration source floor is outside the level geometry.");

            return level.Positions[floor] + logicalWorldOffset;
        }

        throw new NotSupportedException(
            $"Decoration placement '{placement}' requires renderer-specific placement semantics.");
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return null;
    }

    private static int ReadInt32(JsonNode? node, int defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int integer))
                return integer;
            if (value.TryGetValue(out long wide) && wide is >= int.MinValue and <= int.MaxValue)
                return (int)wide;
        }
        return defaultValue;
    }
}
