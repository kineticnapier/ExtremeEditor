using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class TrackAnimationMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, TrackAnimationSettingsData> Cache = new();

    internal static void Attach(LevelDocument level, TrackAnimationSettingsData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static bool TryGet(LevelDocument level, out TrackAnimationSettingsData data)
    {
        if (Cache.TryGetValue(level, out TrackAnimationSettingsData? found))
        {
            data = found;
            return true;
        }
        data = TrackAnimationSettingsData.Default;
        return false;
    }
}

internal static class TrackAnimationTimelineBuilder
{
    internal static (NativeTrackAnimationSegment[] Segments, NativeTrackAnimationTiming[] Timings) Build(
        LevelDocument level,
        TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        int floorCount = Math.Min(level.FloorCount, timingMap.Floors.Count);
        if (floorCount <= 0)
            return ([], []);

        double initialBpm = Math.Max(level.InitialBpm, 0.000001);
        float pitch = (float)Math.Max(0.000001, level.PitchPercent * 0.01);
        var timings = new NativeTrackAnimationTiming[floorCount];
        for (int floor = 0; floor < floorCount; floor++)
        {
            FloorTiming timing = timingMap.Floors[floor];
            double bpm = timing.Bpm > 0.0 ? timing.Bpm : initialBpm;
            timings[floor] = new NativeTrackAnimationTiming
            {
                EntryTime = timing.EntryTime,
                BeatSecondsNoPitch = (float)(60.0 / bpm),
                Speed = (float)(bpm / initialBpm)
            };
        }

        bool hasRootSettings = TrackAnimationMetadataCache.TryGet(level, out TrackAnimationSettingsData initial);
        AnimationAction[] actions = hasRootSettings
            ? initial.Events
                .Where(static action => action.Active)
                .Where(action => (uint)action.Floor < (uint)floorCount)
                .Select(static action => new AnimationAction(
                    action.SourceIndex, action.Floor, action.TrackAnimation, action.BeatsAhead,
                    action.TrackDisappearAnimation, action.BeatsBehind,
                    action.TrackAnimationDisabled, action.TrackDisappearAnimationDisabled))
                .OrderBy(static action => action.Floor)
                .ThenBy(static action => action.SourceIndex)
                .ToArray()
            : level.ActionStore.Actions
                .Where(static action => action.Active &&
                    string.Equals(action.EventType, "AnimateTrack", StringComparison.Ordinal))
                .Where(action => (uint)action.Floor < (uint)floorCount)
                .Select(static action => new AnimationAction(
                    action.SourceIndex,
                    action.Floor,
                    ReadString(action, "trackAnimation"),
                    ReadDouble(action, "beatsAhead"),
                    ReadString(action, "trackDisappearAnimation"),
                    ReadDouble(action, "beatsBehind"),
                    IsDisabled(action, "trackAnimation"),
                    IsDisabled(action, "trackDisappearAnimation")))
                .OrderBy(static action => action.Floor)
                .ThenBy(static action => action.SourceIndex)
                .ToArray();
        string appear = initial.TrackAnimation;
        string disappear = initial.TrackDisappearAnimation;
        double beatsAhead = initial.BeatsAhead;
        double beatsBehind = initial.BeatsBehind;
        var pending = new List<(AnimationAction Action, string Appear, string Disappear, double Ahead, double Behind,
            float ReferenceSpeed, uint Flags)>();

        foreach (AnimationAction action in actions)
        {
            bool appearDisabled = action.AppearDisabled;
            bool disappearDisabled = action.DisappearDisabled;
            if (!appearDisabled && !string.IsNullOrWhiteSpace(action.Appear))
                appear = action.Appear;
            if (!disappearDisabled && !string.IsNullOrWhiteSpace(action.Disappear))
                disappear = action.Disappear;
            if (!appearDisabled && action.Ahead is double nextAhead)
                beatsAhead = Math.Max(0.0, nextAhead);
            if (!disappearDisabled && action.Behind is double nextBehind)
                beatsBehind = Math.Max(0.0, nextBehind);

            float currentSpeed = timings[action.Floor].Speed > 0f ? timings[action.Floor].Speed : 1f;
            float previousSpeed = action.Floor > 0 && timings[action.Floor - 1].Speed > 0f
                ? timings[action.Floor - 1].Speed
                : currentSpeed;
            // Stock selects the same speed reference for both beatsAhead and beatsBehind.
            // The branch is controlled by trackAnimation's disabled flag, even for disappear.
            float selectedReference = appearDisabled ? previousSpeed : currentSpeed;
            uint flags = (appearDisabled ? 0u : NativeTrackAnimationSegment.FlagAppearPropertyEnabled) |
                         (disappearDisabled ? 0u : NativeTrackAnimationSegment.FlagDisappearPropertyEnabled);
            pending.Add((action, appear, disappear, beatsAhead, beatsBehind, selectedReference, flags));
        }

        int rootCount = hasRootSettings && (pending.Count == 0 || pending[0].Action.Floor > 0) ? 1 : 0;
        var result = new NativeTrackAnimationSegment[rootCount + pending.Count];
        if (rootCount != 0)
        {
            int rootEnd = pending.Count == 0 ? floorCount - 1 : pending[0].Action.Floor - 1;
            float referenceSpeed = timings[0].Speed > 0f ? timings[0].Speed : 1f;
            result[0] = new NativeTrackAnimationSegment
            {
                StartFloor = 0,
                EndFloor = rootEnd,
                AppearType = MapAppear(initial.TrackAnimation),
                DisappearType = MapDisappear(initial.TrackDisappearAnimation),
                BeatsAhead = (float)initial.BeatsAhead,
                BeatsBehind = (float)initial.BeatsBehind,
                AppearReferenceSpeed = referenceSpeed,
                DisappearReferenceSpeed = referenceSpeed,
                Pitch = pitch,
                SourceIndex = -1,
                Flags = NativeTrackAnimationSegment.FlagAppearPropertyEnabled |
                        NativeTrackAnimationSegment.FlagDisappearPropertyEnabled
            };
        }

        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            int start = item.Action.Floor;
            int end = i + 1 < pending.Count
                ? Math.Max(start, pending[i + 1].Action.Floor - 1)
                : floorCount - 1;
            result[rootCount + i] = new NativeTrackAnimationSegment
            {
                StartFloor = start,
                EndFloor = end,
                AppearType = MapAppear(item.Appear),
                DisappearType = MapDisappear(item.Disappear),
                BeatsAhead = (float)item.Ahead,
                BeatsBehind = (float)item.Behind,
                AppearReferenceSpeed = item.ReferenceSpeed,
                DisappearReferenceSpeed = item.ReferenceSpeed,
                Pitch = pitch,
                SourceIndex = item.Action.SourceIndex,
                Flags = item.Flags
            };
        }

        return (result, timings);
    }

    internal static uint MapAppear(string? value) => value?.Trim() switch
    {
        "Assemble" => NativeTrackAnimationSegment.AppearAssemble,
        "Assemble_Far" => NativeTrackAnimationSegment.AppearAssembleFar,
        "Extend" => NativeTrackAnimationSegment.AppearExtend,
        "Grow" => NativeTrackAnimationSegment.AppearGrow,
        "Grow_Spin" => NativeTrackAnimationSegment.AppearGrowSpin,
        "Fade" => NativeTrackAnimationSegment.AppearFade,
        "Drop" => NativeTrackAnimationSegment.AppearDrop,
        "Rise" => NativeTrackAnimationSegment.AppearRise,
        _ => NativeTrackAnimationSegment.AppearNone
    };

    internal static uint MapDisappear(string? value) => value?.Trim() switch
    {
        "Scatter" => NativeTrackAnimationSegment.DisappearScatter,
        "Scatter_Far" => NativeTrackAnimationSegment.DisappearScatterFar,
        "Retract" => NativeTrackAnimationSegment.DisappearRetract,
        "Shrink" => NativeTrackAnimationSegment.DisappearShrink,
        "Shrink_Spin" => NativeTrackAnimationSegment.DisappearShrinkSpin,
        "Fade" => NativeTrackAnimationSegment.DisappearFade,
        _ => NativeTrackAnimationSegment.DisappearNone
    };

    private static bool TryString(JsonObject? obj, string name, out string? value)
    {
        value = null;
        if (obj?[name] is not JsonNode node)
            return false;
        try
        {
            value = node.GetValue<string>();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryString(LevelAction action, string name, out string? value) =>
        TryString(action.PropertyOverrides, name, out value) ||
        TryString(action.SourceProperties, name, out value);

    private static bool TryDouble(JsonObject? obj, string name, out double value)
    {
        value = 0.0;
        if (obj?[name] is not JsonNode node)
            return false;
        try
        {
            value = node.GetValue<double>();
            return double.IsFinite(value);
        }
        catch
        {
            if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }
    }

    private static bool TryDouble(LevelAction action, string name, out double value) =>
        TryDouble(action.PropertyOverrides, name, out value) ||
        TryDouble(action.SourceProperties, name, out value);

    private static string? ReadString(LevelAction action, string name) =>
        TryString(action, name, out string? value) ? value : null;

    private static double? ReadDouble(LevelAction action, string name) =>
        TryDouble(action, name, out double value) ? value : null;

    private static bool IsDisabled(LevelAction action, string property)
    {
        JsonNode? value = (action.PropertyOverrides?["disabled"] as JsonObject)?[property] ??
                          (action.SourceProperties?["disabled"] as JsonObject)?[property];
        if (value is null)
            return false;
        try
        {
            return value.GetValue<bool>();
        }
        catch
        {
            string text = value.ToString();
            return string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private readonly record struct AnimationAction(
        int SourceIndex,
        int Floor,
        string? Appear,
        double? Ahead,
        string? Disappear,
        double? Behind,
        bool AppearDisabled,
        bool DisappearDisabled);
}
