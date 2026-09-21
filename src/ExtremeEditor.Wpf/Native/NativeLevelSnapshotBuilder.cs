using System.Diagnostics;
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

internal readonly record struct NativeLevelSnapshotBuildMetrics(
    TimeSpan FloorGeometry,
    TimeSpan Icons,
    TimeSpan Finalize,
    int GeometryCount,
    int IconAssetCount,
    int ActionFloorCount);

internal readonly record struct NativeLevelSnapshotBuildResult(
    NativeLevelSnapshot Snapshot,
    NativeLevelSnapshotBuildMetrics Metrics);

internal static class NativeLevelSnapshotBuilder
{
    private const float TwoPi = MathF.PI * 2f;
    private const float DegreesToRadians = MathF.PI / 180f;

    public static NativeLevelSnapshot Build(LevelDocument level) =>
        BuildProfiled(level).Snapshot;

    internal static NativeLevelSnapshotBuildResult BuildProfiled(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        Vector2[] positions = level.Positions;
        double[] angles = level.Angles;
        var floors = new NativeFloor[positions.Length];
        var geometries = new List<NativeGeometry>();
        var points = new List<NativePoint>(4096);
        var geometryIds = new Dictionary<int, uint>();
        var iconAssets = new List<NativeIconAsset>();

        var watch = Stopwatch.StartNew();

        // Derive screen-space segment angles directly from angleData instead of
        // recovering them from float world positions with two Atan2 calls per floor.
        float previousOutgoingAngle = 0f;
        for (int floor = 0; floor < positions.Length; floor++)
        {
            bool hasAngle = floor < angles.Length;
            bool midSpin = hasAngle && Math.Abs(angles[floor] - 999.0) < 0.000001;

            float entryAngle;
            float exitAngle;
            if (floor == 0)
            {
                if (!hasAngle)
                {
                    entryAngle = MathF.PI;
                    exitAngle = 0f;
                }
                else
                {
                    exitAngle = midSpin
                        ? MathF.PI
                        : LevelAngleToScreenRadians(angles[floor]);
                    entryAngle = AddPi(exitAngle);
                }
            }
            else
            {
                entryAngle = AddPi(previousOutgoingAngle);
                if (!hasAngle)
                    exitAngle = previousOutgoingAngle;
                else if (midSpin)
                    exitAngle = entryAngle;
                else
                    exitAngle = LevelAngleToScreenRadians(angles[floor]);
            }

            previousOutgoingAngle = exitAngle;

            float delta = Mod(exitAngle - entryAngle, TwoPi);
            int quantizedDelta = (int)MathF.Round(delta * 100_000f);
            int geometryKey = (quantizedDelta << 1) | (midSpin ? 1 : 0);

            if (!geometryIds.TryGetValue(geometryKey, out uint geometryId))
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
                geometryIds.Add(geometryKey, geometryId);
            }

            Vector2 position = positions[floor];
            floors[floor] = new NativeFloor
            {
                X = position.X,
                Y = position.Y,
                EntryAngle = entryAngle,
                GeometryId = geometryId,
                IconId = NativeFloor.NoIcon,
                IconFlags = 0u,
                IconAngle = 0f
            };
        }

        watch.Stop();
        TimeSpan floorGeometryTime = watch.Elapsed;

        watch.Restart();
        bool isCcw = false;
        int validActionFloorCount = 0;
        IReadOnlyList<int> actionFloors = level.ActionFloors;
        var floorIconCache = new Dictionary<string, FloorIconAsset?>(StringComparer.OrdinalIgnoreCase);
        var eventIconCache = new Dictionary<string, uint?>(StringComparer.Ordinal);

        // Resolve the two overwhelmingly common Twirl assets once. Path expansion,
        // file checks and long-path hashing must not happen millions of times.
        bool hasSwirlRed = TryGetFloorIconAsset("SwirlRed", iconAssets, floorIconCache, out FloorIconAsset swirlRed);
        bool hasSwirlBlue = TryGetFloorIconAsset("SwirlBlue", iconAssets, floorIconCache, out FloorIconAsset swirlBlue);

        foreach (int floor in actionFloors)
        {
            if ((uint)floor >= (uint)floors.Length ||
                !level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? actions))
                continue;

            validActionFloorCount++;
            bool midSpin = floor < angles.Length && Math.Abs(angles[floor] - 999.0) < 0.000001;
            float entryAngle = floors[floor].EntryAngle;
            float exitAngle = GetExitAngle(floor, angles, entryAngle);

            // Extreme Twirl charts commonly have exactly one Twirl action on nearly
            // every floor. Keep that path branch-free except for the color choice.
            if (actions.Length == 1 && actions[0].Active && actions[0].Kind == LevelActionKind.Twirl)
            {
                isCcw = !isCcw;
                SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
                bool hasAsset = swirl.IsRed ? hasSwirlRed : hasSwirlBlue;
                FloorIconAsset asset = swirl.IsRed ? swirlRed : swirlBlue;
                if (hasAsset)
                {
                    ref NativeFloor nativeFloor = ref floors[floor];
                    nativeFloor.IconId = asset.IconId;
                    nativeFloor.IconFlags = NativeFloor.IconFlagFloor |
                                            (swirl.Flipped ? NativeFloor.IconFlagFlipped : 0u);
                    nativeFloor.IconAngle = swirl.IconAngle;
                    continue;
                }
            }
            else
            {
                foreach (LevelAction action in actions)
                {
                    if (action.Active && action.Kind == LevelActionKind.Twirl)
                        isCcw = !isCcw;
                }
            }

            if (!TryResolveIcon(
                    actions,
                    entryAngle,
                    exitAngle,
                    isCcw,
                    midSpin,
                    iconAssets,
                    floorIconCache,
                    eventIconCache,
                    out ResolvedIcon resolved))
                continue;

            ref NativeFloor target = ref floors[floor];
            target.IconId = resolved.IconId;
            if (resolved.IsFloorIcon)
                target.IconFlags |= NativeFloor.IconFlagFloor;
            if (resolved.Flipped)
                target.IconFlags |= NativeFloor.IconFlagFlipped;
            target.IconAngle = resolved.AngleRadians;
        }

        watch.Stop();
        TimeSpan iconTime = watch.Elapsed;

        watch.Restart();
        WorldRect bounds = level.Bounds;
        var snapshot = new NativeLevelSnapshot
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
        watch.Stop();

        return new NativeLevelSnapshotBuildResult(
            snapshot,
            new NativeLevelSnapshotBuildMetrics(
                floorGeometryTime,
                iconTime,
                watch.Elapsed,
                snapshot.Geometries.Length,
                snapshot.IconAssets.Length,
                validActionFloorCount));
    }

    private static bool TryResolveIcon(
        LevelAction[] actions,
        float entryAngle,
        float exitAngle,
        bool isCcw,
        bool midSpin,
        List<NativeIconAsset> iconAssets,
        Dictionary<string, FloorIconAsset?> floorIconCache,
        Dictionary<string, uint?> eventIconCache,
        out ResolvedIcon resolved)
    {
        resolved = default;
        if (actions.Length == 0)
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
            switch (action.Kind)
            {
                case LevelActionKind.SetFloorIcon when customIconAction is null:
                    customIconAction = action;
                    break;
                case LevelActionKind.Checkpoint:
                    checkpoint = true;
                    break;
                case LevelActionKind.Twirl:
                    twirl = true;
                    break;
                case LevelActionKind.SetSpeed when speedAction is null:
                    speedAction = action;
                    break;
            }
        }

        if (!hasActiveAction)
            return false;

        if (customIconAction?.CustomIcon is { Length: > 0 } customIcon &&
            TryFloorIcon(customIcon, 0f, false, iconAssets, floorIconCache, out resolved))
            return true;

        if (checkpoint && TryFloorIcon("Checkpoint", 0f, false, iconAssets, floorIconCache, out resolved))
            return true;

        if (twirl)
        {
            SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
            if (TryFloorIcon(
                    swirl.IsRed ? "SwirlRed" : "SwirlBlue",
                    swirl.IconAngle,
                    swirl.Flipped,
                    iconAssets,
                    floorIconCache,
                    out resolved))
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
            if (TryFloorIcon(speedIcon, 0f, false, iconAssets, floorIconCache, out resolved))
                return true;
        }

        foreach (LevelAction action in actions)
        {
            if (!action.Active)
                continue;

            if (!eventIconCache.TryGetValue(action.EventType, out uint? iconId))
            {
                string candidate = IconAssetCache.EventPath(action.EventType);
                if (File.Exists(candidate))
                {
                    string fullPath = Path.GetFullPath(candidate);
                    uint id = checked((uint)iconAssets.Count);
                    iconAssets.Add(new NativeIconAsset(id, fullPath, null));
                    iconId = id;
                }
                else
                {
                    iconId = null;
                }
                eventIconCache[action.EventType] = iconId;
            }

            if (iconId is uint resolvedId)
            {
                resolved = new ResolvedIcon(resolvedId, false, 0f, false);
                return true;
            }
        }

        return false;
    }

    private static bool TryFloorIcon(
        string key,
        float angle,
        bool flipped,
        List<NativeIconAsset> iconAssets,
        Dictionary<string, FloorIconAsset?> cache,
        out ResolvedIcon resolved)
    {
        if (!TryGetFloorIconAsset(key, iconAssets, cache, out FloorIconAsset asset))
        {
            resolved = default;
            return false;
        }

        resolved = new ResolvedIcon(asset.IconId, true, angle, flipped);
        return true;
    }

    private static bool TryGetFloorIconAsset(
        string key,
        List<NativeIconAsset> iconAssets,
        Dictionary<string, FloorIconAsset?> cache,
        out FloorIconAsset asset)
    {
        if (cache.TryGetValue(key, out FloorIconAsset? cached))
        {
            if (cached is FloorIconAsset value)
            {
                asset = value;
                return true;
            }

            asset = default;
            return false;
        }

        string imageCandidate = IconAssetCache.FloorPath(key);
        if (!File.Exists(imageCandidate))
        {
            cache[key] = null;
            asset = default;
            return false;
        }

        string imagePath = Path.GetFullPath(imageCandidate);
        string outlineCandidate = IconAssetCache.OutlinePath(key);
        string? outlinePath = File.Exists(outlineCandidate)
            ? Path.GetFullPath(outlineCandidate)
            : null;
        uint iconId = checked((uint)iconAssets.Count);
        iconAssets.Add(new NativeIconAsset(iconId, imagePath, outlinePath));
        asset = new FloorIconAsset(iconId);
        cache[key] = asset;
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

    private static float GetExitAngle(int floor, double[] angles, float entryAngle)
    {
        if (floor >= angles.Length)
            return AddPi(entryAngle);
        if (Math.Abs(angles[floor] - 999.0) < 0.000001)
            return entryAngle;
        return LevelAngleToScreenRadians(angles[floor]);
    }

    private static float LevelAngleToScreenRadians(double angleDegrees)
    {
        double normalized = angleDegrees % 360.0;
        if (normalized < 0.0)
            normalized += 360.0;
        return (float)normalized * DegreesToRadians;
    }

    private static float AddPi(float angle)
    {
        float result = angle + MathF.PI;
        return result >= TwoPi ? result - TwoPi : result;
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly record struct FloorIconAsset(uint IconId);
    private readonly record struct ResolvedIcon(
        uint IconId,
        bool IsFloorIcon,
        float AngleRadians,
        bool Flipped);
    private readonly record struct SwirlVisual(bool IsRed, bool Flipped, float IconAngle);
}
