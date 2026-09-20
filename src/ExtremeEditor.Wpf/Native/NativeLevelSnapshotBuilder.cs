using System.IO;
using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal sealed class NativeLevelSnapshot
{
    public required NativeFloor[] Floors { get; init; }
    public required NativeGeometry[] Geometries { get; init; }
    public required NativePoint[] Points { get; init; }
    public required NativeIconAsset[] IconAssets { get; init; }
    public required float BoundsLeft { get; init; }
    public required float BoundsTop { get; init; }
    public required float BoundsRight { get; init; }
    public required float BoundsBottom { get; init; }
}

internal static class NativeLevelSnapshotBuilder
{
    private const float TwoPi = MathF.PI * 2f;

    public static NativeLevelSnapshot Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        Vector2[] positions = level.Positions;
        var floors = new NativeFloor[positions.Length];
        var geometries = new List<NativeGeometry>();
        var points = new List<NativePoint>(4096);
        var geometryIds = new Dictionary<GeometryKey, uint>();
        var iconAssets = new List<NativeIconAsset>();
        var iconIds = new Dictionary<IconAssetKey, uint>();
        bool isCcw = false;

        for (int floor = 0; floor < positions.Length; floor++)
        {
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < level.Angles.Length && Math.Abs(level.Angles[floor] - 999.0) < 0.000001;
            float delta = Mod(exitAngle - entryAngle, TwoPi);
            var key = new GeometryKey((int)MathF.Round(delta * 100_000f), midSpin);

            if (!geometryIds.TryGetValue(key, out uint geometryId))
            {
                FloorGeometry source = AdoFaiFloorGeometryBuilder.Get(0f, delta, midSpin);
                geometryId = checked((uint)geometries.Count);
                uint pointOffset = checked((uint)points.Count);

                foreach (Vector2 point in source.Main)
                {
                    points.Add(new NativePoint
                    {
                        X = point.X,
                        Y = point.Y
                    });
                }

                geometries.Add(new NativeGeometry
                {
                    PointOffset = pointOffset,
                    PointCount = checked((uint)source.Main.Length)
                });
                geometryIds.Add(key, geometryId);
            }

            LevelAction[]? actions = level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? floorActions)
                ? floorActions
                : null;
            if (actions is not null)
            {
                foreach (LevelAction action in actions)
                {
                    if (action.Active && string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                        isCcw = !isCcw;
                }
            }

            uint iconId = NativeFloor.NoIcon;
            uint iconFlags = 0u;
            float iconAngle = 0f;
            if (TryResolveIcon(actions, entryAngle, exitAngle, isCcw, midSpin, out ResolvedIcon resolved))
            {
                string imagePath = Path.GetFullPath(resolved.ImagePath);
                string? outlinePath = resolved.OutlinePath is { Length: > 0 } outline && File.Exists(outline)
                    ? Path.GetFullPath(outline)
                    : null;
                var iconKey = new IconAssetKey(imagePath, outlinePath);
                if (!iconIds.TryGetValue(iconKey, out iconId))
                {
                    iconId = checked((uint)iconAssets.Count);
                    iconIds.Add(iconKey, iconId);
                    iconAssets.Add(new NativeIconAsset(iconId, imagePath, outlinePath));
                }

                if (resolved.IsFloorIcon)
                    iconFlags |= NativeFloor.IconFlagFloor;
                if (resolved.Flipped)
                    iconFlags |= NativeFloor.IconFlagFlipped;
                iconAngle = resolved.AngleRadians;
            }

            Vector2 position = positions[floor];
            floors[floor] = new NativeFloor
            {
                X = position.X,
                Y = position.Y,
                EntryAngle = entryAngle,
                GeometryId = geometryId,
                IconId = iconId,
                IconFlags = iconFlags,
                IconAngle = iconAngle
            };
        }

        WorldRect bounds = level.Bounds;
        return new NativeLevelSnapshot
        {
            Floors = floors,
            Geometries = geometries.ToArray(),
            Points = points.ToArray(),
            IconAssets = iconAssets.ToArray(),
            BoundsLeft = bounds.Left,
            BoundsTop = bounds.Top,
            BoundsRight = bounds.Right,
            BoundsBottom = bounds.Bottom
        };
    }

    private static bool TryResolveIcon(
        LevelAction[]? actions,
        float entryAngle,
        float exitAngle,
        bool isCcw,
        bool midSpin,
        out ResolvedIcon resolved)
    {
        resolved = default;
        if (actions is null || actions.Length == 0)
            return false;

        LevelAction? customIconAction = null;
        LevelAction? speedAction = null;
        bool checkpoint = false;
        bool twirl = false;
        bool hasActiveAction = false;

        foreach (LevelAction action in actions)
        {
            if (!action.Active)
                continue;

            hasActiveAction = true;
            if (customIconAction is null && string.Equals(action.EventType, "SetFloorIcon", StringComparison.Ordinal))
                customIconAction = action;
            if (string.Equals(action.EventType, "Checkpoint", StringComparison.Ordinal))
                checkpoint = true;
            if (string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                twirl = true;
            if (speedAction is null && string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                speedAction = action;
        }

        if (!hasActiveAction)
            return false;

        if (customIconAction?.CustomIcon is { Length: > 0 } customIcon &&
            TryFloorIcon(customIcon, 0f, false, out resolved))
            return true;

        if (checkpoint && TryFloorIcon("Checkpoint", 0f, false, out resolved))
            return true;

        if (twirl)
        {
            SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
            if (TryFloorIcon(swirl.IsRed ? "SwirlRed" : "SwirlBlue", swirl.IconAngle, swirl.Flipped, out resolved))
                return true;
        }

        if (speedAction?.SpeedRatio is double ratio)
        {
            string speedIcon = ratio switch
            {
                <= 0.45 => "DoubleSnail",
                < 0.95 => "Snail",
                <= 1.05 => "SameSpeed",
                <= 2.05 => "Rabbit",
                _ => "DoubleRabbit"
            };
            if (TryFloorIcon(speedIcon, 0f, false, out resolved))
                return true;
        }

        foreach (LevelAction action in actions)
        {
            if (!action.Active)
                continue;

            string path = IconAssetCache.EventPath(action.EventType);
            if (File.Exists(path))
            {
                resolved = new ResolvedIcon(path, null, false, 0f, false);
                return true;
            }
        }

        return false;
    }

    private static bool TryFloorIcon(string key, float angle, bool flipped, out ResolvedIcon resolved)
    {
        string imagePath = IconAssetCache.FloorPath(key);
        if (!File.Exists(imagePath))
        {
            resolved = default;
            return false;
        }

        resolved = new ResolvedIcon(
            imagePath,
            IconAssetCache.OutlinePath(key),
            true,
            angle,
            flipped);
        return true;
    }

    private static SwirlVisual CalculateSwirlVisual(
        float entryScreenAngle,
        float exitScreenAngle,
        bool isCcw,
        bool midSpin)
    {
        float entry = Mod(TwoPi + MathF.PI / 2f - entryScreenAngle, TwoPi);
        float exit = Mod(TwoPi + MathF.PI / 2f - exitScreenAngle, TwoPi);
        float direction = isCcw ? -1f : 1f;
        float moved = Mod((exit - entry) * direction, TwoPi);
        if (MathF.Abs(moved) <= 0.000001f && !midSpin)
            moved = TwoPi;

        bool isRed = moved < 3.1415918f;
        float iconAngle = entry + moved * 0.5f * direction;
        return new SwirlVisual(isRed, isCcw, iconAngle);
    }

    private static void GetFloorAngles(int floor, Vector2[] positions, out float entryAngle, out float exitAngle)
    {
        Vector2 incoming = Vector2.Zero;
        Vector2 outgoing = Vector2.Zero;

        if (floor > 0)
            incoming = positions[floor - 1] - positions[floor];
        if (floor + 1 < positions.Length)
            outgoing = positions[floor + 1] - positions[floor];

        if (incoming.LengthSquared() < 0.000001f && outgoing.LengthSquared() >= 0.000001f)
            incoming = -outgoing;
        if (outgoing.LengthSquared() < 0.000001f && incoming.LengthSquared() >= 0.000001f)
            outgoing = -incoming;

        entryAngle = incoming.LengthSquared() < 0.000001f
            ? MathF.PI
            : MathF.Atan2(incoming.Y, incoming.X);
        exitAngle = outgoing.LengthSquared() < 0.000001f
            ? 0f
            : MathF.Atan2(outgoing.Y, outgoing.X);
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly record struct GeometryKey(int Delta, bool MidSpin);
    private readonly record struct IconAssetKey(string ImagePath, string? OutlinePath);
    private readonly record struct ResolvedIcon(
        string ImagePath,
        string? OutlinePath,
        bool IsFloorIcon,
        float AngleRadians,
        bool Flipped);
    private readonly record struct SwirlVisual(bool IsRed, bool Flipped, float IconAngle);
}
