using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct FloorIconResolution(
    bool[] CcwBeforeFloor,
    int ValidActionFloorCount);

internal readonly record struct FloorIconSuffixResolution(
    NativeFloorIconState[] States,
    bool[] CcwBeforeFloorSuffix);

internal static class FloorIconResolver
{
    private const float TwoPi = MathF.PI * 2f;

    internal static FloorIconResolution ResolveAll(
        LevelDocument level,
        NativeFloor[] floors,
        List<NativeIconAsset> iconAssets)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(iconAssets);

        var assets = new AssetResolver(iconAssets, allowNewAssets: true);
        var ccwBeforeFloor = new bool[floors.Length];
        bool hasSwirlRed = assets.TryFloorIcon("SwirlRed", 0f, false, out NativeFloorIconState swirlRed);
        bool hasSwirlBlue = assets.TryFloorIcon("SwirlBlue", 0f, false, out NativeFloorIconState swirlBlue);
        bool isCcw = false;
        int cacheCursor = 0;
        int validActionFloorCount = 0;
        LevelActionStore store = level.ActionStore;

        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            int floor = store.GetFloor(actionFloorIndex);
            if ((uint)floor >= (uint)floors.Length)
                continue;

            if (floor > cacheCursor)
                Array.Fill(ccwBeforeFloor, isCcw, cacheCursor, floor - cacheCursor);
            ccwBeforeFloor[floor] = isCcw;
            cacheCursor = floor + 1;
            validActionFloorCount++;

            ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex);
            float entryAngle = floors[floor].EntryAngle;
            float exitAngle = GetExitAngle(floor, level.Angles, entryAngle);
            bool midSpin = IsMidSpin(level.Angles, floor);
            NativeFloorIconState state;
            if (IsSingleActiveTwirl(actions))
            {
                isCcw = !isCcw;
                state = ResolveSingleTwirl(
                    entryAngle,
                    exitAngle,
                    isCcw,
                    midSpin,
                    hasSwirlRed,
                    swirlRed,
                    hasSwirlBlue,
                    swirlBlue);
            }
            else
            {
                ToggleTwirlState(actions, ref isCcw);
                state = ResolveFloor(actions, entryAngle, exitAngle, isCcw, midSpin, assets);
            }
            state.ApplyTo(ref floors[floor]);
        }

        if (cacheCursor < ccwBeforeFloor.Length)
            Array.Fill(ccwBeforeFloor, isCcw, cacheCursor, ccwBeforeFloor.Length - cacheCursor);

        return new FloorIconResolution(ccwBeforeFloor, validActionFloorCount);
    }

    internal static bool TryResolveFrom(
        LevelDocument level,
        NativeLevelSnapshot snapshot,
        int startFloor,
        out FloorIconSuffixResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(snapshot);
        resolution = default;

        int floorCount = snapshot.Floors.Length;
        if ((uint)startFloor >= (uint)floorCount ||
            level.FloorCount != floorCount ||
            snapshot.CcwBeforeFloor.Length != floorCount ||
            snapshot.IconRotationOffsets.Length != floorCount)
        {
            return false;
        }

        var assets = new AssetResolver(snapshot.IconAssets.ToList(), allowNewAssets: false);
        var states = new NativeFloorIconState[floorCount - startFloor];
        var ccwBeforeFloorSuffix = new bool[floorCount - startFloor];
        bool hasSwirlRed = assets.TryFloorIcon("SwirlRed", 0f, false, out NativeFloorIconState swirlRed);
        bool hasSwirlBlue = assets.TryFloorIcon("SwirlBlue", 0f, false, out NativeFloorIconState swirlBlue);
        bool isCcw = snapshot.CcwBeforeFloor[startFloor];
        LevelActionStore store = level.ActionStore;
        int actionFloorIndex = LowerBound(store.Floors, startFloor);

        for (int floor = startFloor; floor < floorCount; floor++)
        {
            ccwBeforeFloorSuffix[floor - startFloor] = isCcw;
            NativeFloorIconState state = NativeFloorIconState.None;
            if (actionFloorIndex < store.ActionFloorCount && store.GetFloor(actionFloorIndex) == floor)
            {
                ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex++);
                float baseEntryAngle = snapshot.Floors[floor].EntryAngle - snapshot.IconRotationOffsets[floor];
                float exitAngle = GetExitAngle(floor, level.Angles, baseEntryAngle);
                bool midSpin = IsMidSpin(level.Angles, floor);
                if (IsSingleActiveTwirl(actions))
                {
                    isCcw = !isCcw;
                    state = ResolveSingleTwirl(
                        baseEntryAngle,
                        exitAngle,
                        isCcw,
                        midSpin,
                        hasSwirlRed,
                        swirlRed,
                        hasSwirlBlue,
                        swirlBlue);
                }
                else
                {
                    ToggleTwirlState(actions, ref isCcw);
                    state = ResolveFloor(actions, baseEntryAngle, exitAngle, isCcw, midSpin, assets);
                }
                if (state.IconId != NativeFloor.NoIcon)
                    state.IconAngle += snapshot.IconRotationOffsets[floor];
            }
            states[floor - startFloor] = state;
        }

        int terminalFloor = floorCount - 1;
        if (terminalFloor >= startFloor)
        {
            if (TerminalPortalResolver.TryResolveExisting(
                    snapshot.IconAssets,
                    out NativeFloorIconState portalState))
            {
                states[terminalFloor - startFloor] = portalState;
            }
            else if (File.Exists(IconAssetCache.FloorPath("Portal")))
            {
                return false;
            }
        }

        if (assets.RequiredAssetUnavailable)
            return false;

        resolution = new FloorIconSuffixResolution(states, ccwBeforeFloorSuffix);
        return true;
    }

    private static NativeFloorIconState ResolveFloor(
        ReadOnlySpan<LevelAction> actions,
        float entryAngle,
        float exitAngle,
        bool isCcw,
        bool midSpin,
        AssetResolver assets)
    {
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
            return NativeFloorIconState.None;

        if (customIconAction?.CustomIcon is { Length: > 0 } customIcon &&
            assets.TryFloorIcon(customIcon, 0f, false, out NativeFloorIconState custom))
            return custom;

        if (checkpoint && assets.TryFloorIcon("Checkpoint", 0f, false, out NativeFloorIconState checkpointState))
            return checkpointState;

        if (twirl)
        {
            SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
            if (assets.TryFloorIcon(
                    swirl.IsRed ? "SwirlRed" : "SwirlBlue",
                    swirl.IconAngle,
                    swirl.Flipped,
                    out NativeFloorIconState swirlState))
                return swirlState;
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
            if (assets.TryFloorIcon(speedIcon, 0f, false, out NativeFloorIconState speedState))
                return speedState;
        }

        foreach (LevelAction action in actions)
        {
            if (action.Active && assets.TryEventIcon(action.EventType, out NativeFloorIconState eventState))
                return eventState;
        }

        return NativeFloorIconState.None;
    }

    private static bool IsSingleActiveTwirl(ReadOnlySpan<LevelAction> actions) =>
        actions.Length == 1 && actions[0].Active && actions[0].Kind == LevelActionKind.Twirl;

    private static NativeFloorIconState ResolveSingleTwirl(
        float entryAngle,
        float exitAngle,
        bool isCcw,
        bool midSpin,
        bool hasSwirlRed,
        NativeFloorIconState swirlRed,
        bool hasSwirlBlue,
        NativeFloorIconState swirlBlue)
    {
        SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
        bool available = swirl.IsRed ? hasSwirlRed : hasSwirlBlue;
        if (!available)
            return NativeFloorIconState.None;

        NativeFloorIconState state = swirl.IsRed ? swirlRed : swirlBlue;
        state.IconAngle = swirl.IconAngle;
        if (swirl.Flipped)
            state.IconFlags |= NativeFloor.IconFlagFlipped;
        return state;
    }

    private static void ToggleTwirlState(ReadOnlySpan<LevelAction> actions, ref bool isCcw)
    {
        foreach (LevelAction action in actions)
        {
            if (action.Active && action.Kind == LevelActionKind.Twirl)
                isCcw = !isCcw;
        }
    }

    private static int LowerBound(IReadOnlyList<int> values, int target)
    {
        int low = 0;
        int high = values.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] < target)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static bool IsMidSpin(double[] angles, int floor) =>
        floor < angles.Length && Math.Abs(angles[floor] - 999.0) < 0.000001;

    private static float GetExitAngle(int floor, double[] angles, float entryAngle)
    {
        if (floor >= angles.Length)
            return AddPi(entryAngle);
        if (Math.Abs(angles[floor] - 999.0) < 0.000001)
            return entryAngle;
        return LevelAngleToScreenRadians(angles[floor]);
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

        return new SwirlVisual(
            moved < 3.1415918f,
            isCcw,
            entry + moved * 0.5f * direction);
    }

    private static float LevelAngleToScreenRadians(double angleDegrees)
    {
        double normalized = angleDegrees % 360.0;
        if (normalized < 0.0)
            normalized += 360.0;
        return (float)normalized * (MathF.PI / 180f);
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

    private readonly record struct SwirlVisual(bool IsRed, bool Flipped, float IconAngle);

    private sealed class AssetResolver
    {
        private readonly List<NativeIconAsset> _assets;
        private readonly bool _allowNewAssets;
        private readonly Dictionary<string, NativeFloorIconState?> _floorCache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NativeFloorIconState?> _eventCache =
            new(StringComparer.Ordinal);

        internal AssetResolver(List<NativeIconAsset> assets, bool allowNewAssets)
        {
            _assets = assets;
            _allowNewAssets = allowNewAssets;
        }

        internal bool RequiredAssetUnavailable { get; private set; }

        internal bool TryFloorIcon(
            string key,
            float angle,
            bool flipped,
            out NativeFloorIconState state)
        {
            if (!_floorCache.TryGetValue(key, out NativeFloorIconState? cached))
            {
                string imageCandidate = IconAssetCache.FloorPath(key);
                if (!File.Exists(imageCandidate))
                {
                    _floorCache[key] = null;
                    state = default;
                    return false;
                }

                string imagePath = Path.GetFullPath(imageCandidate);
                string outlineCandidate = IconAssetCache.OutlinePath(key);
                string? outlinePath = File.Exists(outlineCandidate) ? Path.GetFullPath(outlineCandidate) : null;
                uint? id = FindAsset(imagePath, outlinePath);
                if (id is null && _allowNewAssets)
                {
                    id = checked((uint)_assets.Count);
                    _assets.Add(NativeIconAsset.Create(id.Value, imagePath, outlinePath));
                }
                if (id is null)
                {
                    RequiredAssetUnavailable = true;
                    _floorCache[key] = null;
                    state = default;
                    return false;
                }

                cached = new NativeFloorIconState
                {
                    IconId = id.Value,
                    IconFlags = NativeFloor.IconFlagFloor
                };
                _floorCache[key] = cached;
            }

            if (cached is not NativeFloorIconState value)
            {
                state = default;
                return false;
            }
            state = value;
            state.IconAngle = angle;
            if (flipped)
                state.IconFlags |= NativeFloor.IconFlagFlipped;
            return true;
        }

        internal bool TryEventIcon(string eventType, out NativeFloorIconState state)
        {
            if (!_eventCache.TryGetValue(eventType, out NativeFloorIconState? cached))
            {
                string candidate = IconAssetCache.EventPath(eventType);
                if (!File.Exists(candidate))
                {
                    _eventCache[eventType] = null;
                    state = default;
                    return false;
                }

                string fullPath = Path.GetFullPath(candidate);
                uint? id = FindAsset(fullPath, null);
                if (id is null && _allowNewAssets)
                {
                    id = checked((uint)_assets.Count);
                    _assets.Add(NativeIconAsset.Create(id.Value, fullPath, null));
                }
                if (id is null)
                {
                    RequiredAssetUnavailable = true;
                    _eventCache[eventType] = null;
                    state = default;
                    return false;
                }
                cached = new NativeFloorIconState { IconId = id.Value };
                _eventCache[eventType] = cached;
            }

            if (cached is NativeFloorIconState value)
            {
                state = value;
                return true;
            }
            state = default;
            return false;
        }

        private uint? FindAsset(string imagePath, string? outlinePath)
        {
            foreach (NativeIconAsset asset in _assets)
            {
                if (string.Equals(asset.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(asset.OutlinePath, outlinePath, StringComparison.OrdinalIgnoreCase))
                    return asset.Id;
            }
            return null;
        }
    }
}
