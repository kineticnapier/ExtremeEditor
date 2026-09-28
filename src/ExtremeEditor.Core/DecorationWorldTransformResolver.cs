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

    public static Vector2 ResolveWorldPosition(
        LevelDocument level,
        LevelDecoration decoration,
        DecorationState state,
        Vector2 cameraNow,
        Vector2 cameraAtStart)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(state);

        Vector2 world = ResolveWorldPositionCore(level, decoration, state, null);
        Vector2 parallax = ReadVector2(decoration.Properties["parallax"], Vector2.Zero);

        if (parallax == Vector2.Zero)
            return world;

        Vector2 multiplier = parallax / 100f;
        Vector2 parallaxOffset = ReadVector2(decoration.Properties["parallaxOffset"], Vector2.Zero)
                                 * PathBuilder.DefaultLongTileSize;
        Vector2 cameraDelta = cameraNow - cameraAtStart;

        return world + cameraDelta * multiplier + parallaxOffset;
    }

    public static Vector2 ResolveScreenRelativePosition(
        LevelDecoration decoration,
        DecorationState state,
        float screenWidth,
        float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(state);
        if (!float.IsFinite(screenWidth) || screenWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(screenWidth));
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(screenHeight));

        string placement = ReadString(decoration.Properties["relativeTo"]) ?? "Tile";
        bool isCamera = string.Equals(placement, "Camera", StringComparison.OrdinalIgnoreCase);
        bool isCameraAspect = string.Equals(placement, "CameraAspect", StringComparison.OrdinalIgnoreCase);
        if (!isCamera && !isCameraAspect)
        {
            throw new NotSupportedException(
                $"Decoration placement '{placement}' does not use camera screen-clamp semantics.");
        }

        Vector2 pivotOffset = ReadVector2(decoration.Properties["pivotOffset"], Vector2.Zero);
        Vector2 logical = new(
            checked((float)state.PositionX),
            checked((float)state.PositionY));
        Vector2 screenRelative = logical + pivotOffset;

        if (isCameraAspect)
            screenRelative.X *= screenHeight / screenWidth;

        return screenRelative / 20f + new Vector2(0.5f, 0.5f);
    }

    public static Vector2 ResolveStickToFloorWorldPosition(
        LevelDecoration decoration,
        Vector2 baseWorldPosition,
        Vector2 parentFloorWorldPosition,
        Vector2 startPosition)
    {
        ArgumentNullException.ThrowIfNull(decoration);

        bool stickToFloor = ReadBool(decoration.Properties["stickToFloor"], false);
        if (!stickToFloor)
            return baseWorldPosition;

        // Stock scrDecoration.UpdatePosition semantics:
        // pivotPosVec is resolved independently, then the current parent floor delta
        // since setup is applied dynamically. Do not bake this runtime movement back
        // into DecorationState or the base placement resolver.
        return baseWorldPosition + parentFloorWorldPosition - startPosition;
    }

    public static float ResolveStickToFloorRotation(
        LevelDecoration decoration,
        float baseRotation,
        float parentFloorRotation)
    {
        ArgumentNullException.ThrowIfNull(decoration);
        if (!float.IsFinite(baseRotation))
            throw new ArgumentOutOfRangeException(nameof(baseRotation));
        if (!float.IsFinite(parentFloorRotation))
            throw new ArgumentOutOfRangeException(nameof(parentFloorRotation));

        if (!ReadBool(decoration.Properties["stickToFloor"], false))
            return baseRotation;

        // Stock scrDecoration.SetRotation: when stickToFloor is enabled the current
        // parent-floor Z rotation is added each time UpdateLock refreshes the transform.
        return baseRotation + parentFloorRotation;
    }

    public static Vector2 ResolveStickToFloorScale(
        LevelDecoration decoration,
        Vector2 baseScale,
        Vector2 parentFloorScale)
    {
        ArgumentNullException.ThrowIfNull(decoration);
        if (!float.IsFinite(baseScale.X) || !float.IsFinite(baseScale.Y))
            throw new ArgumentOutOfRangeException(nameof(baseScale));
        if (!float.IsFinite(parentFloorScale.X) || !float.IsFinite(parentFloorScale.Y))
            throw new ArgumentOutOfRangeException(nameof(parentFloorScale));

        if (!ReadBool(decoration.Properties["stickToFloor"], false))
            return baseScale;

        // Stock scrDecoration.SetScale multiplies by parentFloor.transform.localScale
        // component-wise for stickToFloor decorations. Camera lock scaling remains a
        // separate renderer concern and is intentionally not baked into this helper.
        return baseScale * parentFloorScale;
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

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is JsonValue value && value.TryGetValue(out bool result))
            return result;
        return defaultValue;
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

    private static Vector2 ReadVector2(JsonNode? node, Vector2 defaultValue)
    {
        if (node is not JsonArray array || array.Count < 2)
            return defaultValue;

        if (!TryReadSingle(array[0], out float x) || !TryReadSingle(array[1], out float y))
            return defaultValue;

        return new Vector2(x, y);
    }

    private static bool TryReadSingle(JsonNode? node, out float value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out float single))
            {
                value = single;
                return float.IsFinite(value);
            }

            if (jsonValue.TryGetValue(out double wide) && double.IsFinite(wide) &&
                wide is >= -float.MaxValue and <= float.MaxValue)
            {
                value = (float)wide;
                return true;
            }

            if (jsonValue.TryGetValue(out int integer))
            {
                value = integer;
                return true;
            }
        }

        value = 0f;
        return false;
    }
}
