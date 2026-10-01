using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

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

        var actions = level.ActionStore.Actions
            .Where(static action => action.Active &&
                string.Equals(action.EventType, "AnimateTrack", StringComparison.Ordinal))
            .Where(action => (uint)action.Floor < (uint)floorCount)
            .OrderBy(static action => action.Floor)
            .ThenBy(static action => action.SourceIndex)
            .ToArray();
        if (actions.Length == 0)
            return ([], timings);

        string appear = "None";
        string disappear = "None";
        double beatsAhead = 3.0;
        double beatsBehind = 4.0;
        var pending = new List<(LevelAction Action, string Appear, string Disappear, double Ahead, double Behind)>();

        foreach (LevelAction action in actions)
        {
            JsonObject? obj = action.PropertyOverrides;
            if (TryString(obj, "trackAnimation", out string? nextAppear))
                appear = nextAppear!;
            if (TryString(obj, "trackDisappearAnimation", out string? nextDisappear))
                disappear = nextDisappear!;
            if (TryDouble(obj, "beatsAhead", out double nextAhead))
                beatsAhead = Math.Max(0.0, nextAhead);
            if (TryDouble(obj, "beatsBehind", out double nextBehind))
                beatsBehind = Math.Max(0.0, nextBehind);

            pending.Add((action, appear, disappear, beatsAhead, beatsBehind));
        }

        var result = new NativeTrackAnimationSegment[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            int start = item.Action.Floor;
            int end = i + 1 < pending.Count
                ? Math.Max(start, pending[i + 1].Action.Floor - 1)
                : floorCount - 1;
            float referenceSpeed = timings[start].Speed > 0f ? timings[start].Speed : 1f;
            result[i] = new NativeTrackAnimationSegment
            {
                StartFloor = start,
                EndFloor = end,
                AppearType = MapAppear(item.Appear),
                DisappearType = MapDisappear(item.Disappear),
                BeatsAhead = (float)item.Ahead,
                BeatsBehind = (float)item.Behind,
                AppearReferenceSpeed = referenceSpeed,
                DisappearReferenceSpeed = referenceSpeed,
                Pitch = pitch,
                SourceIndex = item.Action.SourceIndex
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
}
