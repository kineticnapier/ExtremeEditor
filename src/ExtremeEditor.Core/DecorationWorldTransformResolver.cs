using System.Numerics;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// Converts the chart-space decoration position produced by <see cref="DecorationState"/>
/// into stock-game world-space placement. Camera/parallax-specific composition remains a
/// renderer concern; planet-follow placement receives the current planet world position
/// from the caller so logical decoration state stays independent from runtime follow state.
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

        return ResolveWorldPositionCore(level, decoration, state, null);
    }

    public static Vector2 ResolveWorldPosition(
        LevelDocument level,
        LevelDecoration decoration,
        DecorationState state,
        Func<string, Vector2> planetWorldPositionResolver)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(planetWorldPositionResolver);

        return ResolveWorldPositionCore(level, decoration, state, planetWorldPositionResolver);
    }

    private static Vector2 ResolveWorldPositionCore(
        LevelDocument level,
        LevelDecoration decoration,
        DecorationState state,
        Func<string, Vector2>? planetWorldPositionResolver)
    {
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

        if (IsPlanetPlacement(placement))
        {
            if (planetWorldPositionResolver is null)
            {
                throw new NotSupportedException(
                    $"Decoration placement '{placement}' requires the current planet world position.");
            }

            return planetWorldPositionResolver(placement) + logicalWorldOffset;
        }

        throw new NotSupportedException(
            $"Decoration placement '{placement}' requires renderer-specific placement semantics.");
    }

    private static bool IsPlanetPlacement(string placement) =>
        string.Equals(placement, "RedPlanet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(placement, "BluePlanet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(placement, "GreenPlanet", StringComparison.OrdinalIgnoreCase);

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
