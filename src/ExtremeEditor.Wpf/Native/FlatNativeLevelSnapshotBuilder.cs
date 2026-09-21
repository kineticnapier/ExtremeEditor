using System.Diagnostics;
using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal static class FlatNativeLevelSnapshotBuilder
{
    private const float TwoPi = MathF.PI * 2f;
    private const float DegreesToRadians = MathF.PI / 180f;

    internal static NativeLevelSnapshotBuildResult BuildProfiled(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        LevelActionStore empty = LevelActionStore.Empty;
        var geometryOnlyLevel = new LevelDocument
        {
            SourcePath = level.SourcePath,
            Angles = level.Angles,
            Positions = level.Positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = empty.DictionaryView,
            ActionStore = empty,
            InitialBpm = level.InitialBpm,
            SongFilename = level.SongFilename,
            OffsetMilliseconds = level.OffsetMilliseconds,
            PitchPercent = level.PitchPercent,
            CountdownTicks = level.CountdownTicks,
            SeparateCountdownTime = level.SeparateCountdownTime,
            DefaultHitSound = level.DefaultHitSound,
            HitSoundVolumePercent = level.HitSoundVolumePercent,
            Bounds = level.Bounds
        };

        NativeLevelSnapshotBuildResult geometryResult =
            NativeLevelSnapshotBuilder.BuildProfiled(geometryOnlyLevel);
        NativeLevelSnapshot geometrySnapshot = geometryResult.Snapshot;
        NativeFloor[] floors = geometrySnapshot.Floors;
        double[] angles = level.Angles;
        uint[] trackColorFlags = TrackColorVisualResolver.ResolveFlags(level);

        var watch = Stopwatch.StartNew();
        var iconAssets = new List<NativeIconAsset>();
        var floorIconCache = new Dictionary<string, FloorIconAsset?>(StringComparer.OrdinalIgnoreCase);
        var eventIconCache = new Dictionary<string, uint?>(StringComparer.Ordinal);

        bool hasSwirlRed = TryGetFloorIconAsset(
            "SwirlRed", iconAssets, floorIconCache, out FloorIconAsset swirlRed);
        bool hasSwirlBlue = TryGetFloorIconAsset(
            "SwirlBlue", iconAssets, floorIconCache, out FloorIconAsset swirlBlue);

        bool isCcw = false;
        int validActionFloorCount = 0;
        LevelActionStore store = level.ActionStore;
        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            int floor = store.GetFloor(actionFloorIndex);
            if ((uint)floor >= (uint)floors.Length)
                continue;

            validActionFloorCount++;
            ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex);
            bool midSpin = floor < angles.Length && Math.Abs(angles[floor] - 999.0) < 0.000001;
            float entryAngle = floors[floor].EntryAngle;
            float exitAngle = GetExitAngle(floor, angles, entryAngle);

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

        // Track colour is packed into the otherwise-unused high bits of icon_flags.
        // Apply it after icon resolution so the Twirl fast-path assignment above
        // cannot discard the visual state.
        int trackColorCount = Math.Min(floors.Length, trackColorFlags.Length);
        for (int floor = 0; floor < trackColorCount; floor++)
            floors[floor].IconFlags |= trackColorFlags[floor];

        watch.Stop();
        TimeSpan iconTime = watch.Elapsed;

        watch.Restart();
        var snapshot = new NativeLevelSnapshot
        {
            Floors = floors,
            Geometries = geometrySnapshot.Geometries,
            Points = geometrySnapshot.Points,
            IconAssets = iconAssets.ToArray(),
            BoundsLeft = geometrySnapshot.BoundsLeft,
            BoundsTop = geometrySnapshot.BoundsTop,
            BoundsRight = geometrySnapshot.BoundsRight,
            BoundsBottom = geometrySnapshot.BoundsBottom
        };
        watch.Stop();

        NativeLevelSnapshotBuildMetrics baseMetrics = geometryResult.Metrics;
        return new NativeLevelSnapshotBuildResult(
            snapshot,
            new NativeLevelSnapshotBuildMetrics(
                baseMetrics.FloorGeometry,
                iconTime,
                baseMetrics.Finalize + watch.Elapsed,
                snapshot.Geometries.Length,
                snapshot.IconAssets.Length,
                validActionFloorCount));
    }

    private static bool TryResolveIcon(
        ReadOnlySpan<LevelAction> actions,
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

        if (checkpoint &&
            TryFloorIcon("Checkpoint", 0f, false, iconAssets, floorIconCache, out resolved))
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
