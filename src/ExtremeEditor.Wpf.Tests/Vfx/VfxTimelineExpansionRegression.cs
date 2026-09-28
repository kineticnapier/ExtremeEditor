using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class VfxTimelineExpansionRegression
{
    private const double Tolerance = 1e-9;

    public static void Run()
    {
        LevelDocument level = CreateFixture();
        TimingMap timing = TimingMapBuilder.Build(level);
        LevelSnapshot before = LevelSnapshot.Create(level);

        VfxTimeline first = VfxTimelineBuilder.Build(level);
        VfxTimeline second = VfxTimelineBuilder.Build(level);

        // Check the new RED-B seam first so current GREEN-A fails for one
        // precise reason instead of failing later on a derived count.
        RequireResolvedStartTime(first.Occurrences);

        VerifySetStartTime(level, timing, first.Occurrences);
        VerifyBeatRepeat(first);
        VerifyFloorRepeatWithoutFloorMove(timing, first);
        VerifyFloorRepeatWithFloorMove(first);
        VerifyOutOfRangeStops(first, level.FloorCount);
        VerifyMultipleTagsAndInactiveRepeat(first);
        VerifyStockOrdering(first.Occurrences);
        VerifyDeterministicEnumeration(first, second);
        VerifySourceWasNotMutated(level, before);
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(9);
        level.InitialBpm = 120;
        level.PitchPercent = 73;

        var speed = new LevelAction(1, "SetSpeed", true, "Multiplier", null, 1.5, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 0,
            SpeedRatio = 1.5
        };
        LevelAction timingProbe = CreateVfx(1, 1, 90, "timing-probe");

        LevelAction beatTarget = CreateVfx(
            2,
            2,
            15,
            "inactive-tag beat-first beat-second");
        LevelAction inactiveRepeat = CreateRepeat(
            2, 3, "Beat", "inactive-tag", active: false,
            repetitions: 8, interval: 8, floorCount: null, executeOnCurrentFloor: false);
        LevelAction selectedBeatRepeat = CreateRepeat(
            2, 4, "Beat", "beat-first", active: true,
            repetitions: 3, interval: 0.5, floorCount: null, executeOnCurrentFloor: false);
        LevelAction laterBeatRepeat = CreateRepeat(
            2, 5, "Beat", "beat-second", active: true,
            repetitions: 1, interval: 9, floorCount: null, executeOnCurrentFloor: false);

        LevelAction fixedFloorTarget = CreateVfx(3, 6, 10, "floor-fixed");
        LevelAction fixedFloorRepeat = CreateRepeat(
            3, 7, "Floor", "floor-fixed", active: true,
            repetitions: null, interval: null, floorCount: 2, executeOnCurrentFloor: false);

        LevelAction movingFloorTarget = CreateVfx(4, 8, 20, "floor-moving");
        LevelAction movingFloorRepeat = CreateRepeat(
            4, 9, "Floor", "floor-moving", active: true,
            repetitions: null, interval: null, floorCount: 2, executeOnCurrentFloor: true);

        LevelAction boundaryTarget = CreateVfx(7, 10, 5, "floor-boundary");
        LevelAction boundaryRepeat = CreateRepeat(
            7, 11, "Floor", "floor-boundary", active: true,
            repetitions: null, interval: null, floorCount: 4, executeOnCurrentFloor: true);

        level.ReplaceActions(
        [
            speed,
            timingProbe,
            beatTarget,
            inactiveRepeat,
            selectedBeatRepeat,
            laterBeatRepeat,
            fixedFloorTarget,
            fixedFloorRepeat,
            movingFloorTarget,
            movingFloorRepeat,
            boundaryTarget,
            boundaryRepeat
        ]);
        return level;
    }

    private static LevelAction CreateVfx(
        int floor,
        int sourceIndex,
        double angleOffset,
        string eventTag)
    {
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = 1.25,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "MoveDecorations",
                ["active"] = true,
                ["angleOffset"] = angleOffset,
                ["duration"] = 1.25,
                ["ease"] = "InOutSine",
                ["eventTag"] = eventTag
            }
        };
    }

    private static LevelAction CreateRepeat(
        int floor,
        int sourceIndex,
        string repeatType,
        string tag,
        bool active,
        int? repetitions,
        double? interval,
        int? floorCount,
        bool executeOnCurrentFloor)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "RepeatEvents",
            ["active"] = active,
            ["repeatType"] = repeatType,
            ["tag"] = tag,
            ["executeOnCurrentFloor"] = executeOnCurrentFloor
        };
        if (repetitions is int repeatCount)
            properties["repetitions"] = repeatCount;
        if (interval is double repeatInterval)
            properties["interval"] = repeatInterval;
        if (floorCount is int repeatFloorCount)
            properties["floorCount"] = repeatFloorCount;

        return new LevelAction(floor, "RepeatEvents", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = properties
        };
    }

    private static void RequireResolvedStartTime(IReadOnlyList<VfxOccurrence> occurrences)
    {
        if (occurrences.Count == 0)
            throw new InvalidOperationException("VFX timeline returned no source occurrences.");

        VfxOccurrence timingProbe = occurrences.Single(item =>
            item.SourceIndex == 1 && !item.IsRepeated);
        _ = ReadRequiredDouble(timingProbe, "StartTime", "StartTimeSeconds");
    }

    private static void VerifySetStartTime(
        LevelDocument level,
        TimingMap timing,
        IReadOnlyList<VfxOccurrence> occurrences)
    {
        VfxOccurrence occurrence = occurrences.Single(item =>
            item.SourceIndex == 1 && !item.IsRepeated);
        FloorTiming floor = timing.Floors[occurrence.Floor];
        double floorSpeed = floor.Bpm / level.InitialBpm;
        double expected = floor.EntryTime +
                          occurrence.AngleOffset.GetValueOrDefault() / 180.0 *
                          (60.0 / (level.InitialBpm * floorSpeed));
        AssertNear(expected, ReadStartTime(occurrence),
            "VFX startTime does not use floor.entryTime, bpm, floor.speed, and degreeOffset.");
    }

    private static void VerifyBeatRepeat(VfxTimeline timeline)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 2);
        if (occurrences.Length != 4)
        {
            throw new InvalidOperationException(
                $"Beat Repeat must include i=0 through repetitions. expected=4 actual={occurrences.Length}.");
        }

        AssertSequenceEqual([2, 2, 2, 2], occurrences.Select(item => item.Floor),
            "Beat Repeat changed the source floor.");
        AssertNearSequence([15, 105, 195, 285], occurrences.Select(EffectiveAngleOffset),
            "Beat Repeat did not apply interval * i * 180 degrees.");
    }

    private static void VerifyFloorRepeatWithoutFloorMove(
        TimingMap timing,
        VfxTimeline timeline)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 6);
        if (occurrences.Length != 3)
        {
            throw new InvalidOperationException(
                $"Floor Repeat must include i=0 through floorCount. expected=3 actual={occurrences.Length}.");
        }

        AssertSequenceEqual([3, 3, 3], occurrences.Select(item => item.Floor),
            "Floor Repeat with executeOnCurrentFloor=false changed the source floor.");

        double[] entryBeats = BuildEntryBeats(timing);
        double sourceBeat = entryBeats[3];
        double[] expected =
        [
            10,
            10 + (entryBeats[4] - sourceBeat) * 180,
            10 + (entryBeats[5] - sourceBeat) * 180
        ];
        AssertNearSequence(expected, occurrences.Select(EffectiveAngleOffset),
            "Floor Repeat did not convert the entryBeat difference to degree offset.");
    }

    private static void VerifyFloorRepeatWithFloorMove(VfxTimeline timeline)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 8);
        if (occurrences.Length != 3)
        {
            throw new InvalidOperationException(
                $"Moving Floor Repeat must include i=0 through floorCount. expected=3 actual={occurrences.Length}.");
        }

        AssertSequenceEqual([4, 5, 6], occurrences.Select(item => item.Floor),
            "Floor Repeat with executeOnCurrentFloor=true did not move targetFloor by i.");
        AssertNearSequence([20, 20, 20], occurrences.Select(EffectiveAngleOffset),
            "Moving Floor Repeat added a repeat-derived degree offset.");
    }

    private static void VerifyOutOfRangeStops(VfxTimeline timeline, int floorCount)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 10);
        AssertSequenceEqual([7, 8], occurrences.Select(item => item.Floor),
            "Floor Repeat did not stop when targetFloor left the document.");
        if (occurrences.Any(item => item.Floor < 0 || item.Floor >= floorCount))
            throw new InvalidOperationException("Floor Repeat emitted an out-of-range occurrence.");
    }

    private static void VerifyMultipleTagsAndInactiveRepeat(VfxTimeline timeline)
    {
        if (timeline.Repeats.Any(repeat => repeat.SourceIndex == 3))
            throw new InvalidOperationException("active:false RepeatEvents was not ignored.");

        VfxOccurrence[] derived = ForSource(timeline, 2)
            .Where(item => item.IsRepeated)
            .ToArray();
        if (derived.Length == 0 || derived.Any(item => item.RepeatSourceIndex != 4))
        {
            throw new InvalidOperationException(
                "A multi-tag event did not select its first matching active RepeatEvents definition.");
        }
    }

    private static void VerifyStockOrdering(IReadOnlyList<VfxOccurrence> occurrences)
    {
        for (int i = 1; i < occurrences.Count; i++)
        {
            VfxOccurrence previous = occurrences[i - 1];
            VfxOccurrence current = occurrences[i];
            double previousKey = ReadStartTime(previous) - ReadOptionalDouble(previous, "StartEffectOffset");
            double currentKey = ReadStartTime(current) - ReadOptionalDouble(current, "StartEffectOffset");
            if (previousKey > currentKey + Tolerance ||
                (Math.Abs(previousKey - currentKey) <= Tolerance && previous.Floor > current.Floor))
            {
                throw new InvalidOperationException(
                    "VFX occurrences are not ordered by startTime - startEffectOffset, then floor.");
            }
            // A complete tie intentionally has no asserted order here.
        }
    }

    private static void VerifyDeterministicEnumeration(VfxTimeline first, VfxTimeline second)
    {
        string[] firstOrder = first.Occurrences.Select(Signature).ToArray();
        string[] secondOrder = second.Occurrences.Select(Signature).ToArray();
        if (!firstOrder.SequenceEqual(secondOrder, StringComparer.Ordinal))
            throw new InvalidOperationException("Expanded VFX occurrence ordering is not deterministic.");
    }

    private static void VerifySourceWasNotMutated(LevelDocument level, LevelSnapshot before)
    {
        if (!before.Matches(LevelSnapshot.Create(level)))
            throw new InvalidOperationException("VFX expansion mutated the source LevelEvent or LevelDocument.");
    }

    private static VfxOccurrence[] ForSource(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Where(item => item.SourceIndex == sourceIndex).ToArray();

    private static double[] BuildEntryBeats(TimingMap timing)
    {
        var result = new double[timing.Floors.Count];
        for (int floor = 1; floor < result.Length; floor++)
            result[floor] = result[floor - 1] + timing.Floors[floor - 1].AngleMoved / Math.PI;
        return result;
    }

    private static double EffectiveAngleOffset(VfxOccurrence occurrence) =>
        occurrence.AngleOffset.GetValueOrDefault();

    private static double ReadStartTime(VfxOccurrence occurrence) =>
        ReadRequiredDouble(occurrence, "StartTime", "StartTimeSeconds");

    private static double ReadRequiredDouble(object source, params string[] names)
    {
        object? value = ReadProperty(source, names);
        if (value is double number)
            return number;
        if (value is float single)
            return single;
        throw new InvalidOperationException(
            "VFX occurrence does not expose resolved StartTime.");
    }

    private static double ReadOptionalDouble(object source, params string[] names)
    {
        object? value = ReadProperty(source, names);
        return value switch
        {
            double number => number,
            float single => single,
            _ => 0
        };
    }

    private static object? ReadProperty(object source, params string[] names)
    {
        Type type = source.GetType();
        foreach (string name in names)
        {
            PropertyInfo? property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is not null)
                return property.GetValue(source);
        }
        return null;
    }

    private static string Signature(VfxOccurrence occurrence) => string.Join(
        "|",
        occurrence.Floor,
        occurrence.SourceIndex,
        occurrence.EventType,
        occurrence.AngleOffset,
        occurrence.IsRepeated,
        occurrence.RepeatSourceIndex,
        ReadStartTime(occurrence));

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > Tolerance)
            throw new InvalidOperationException($"{message} expected={expected:R} actual={actual:R}.");
    }

    private static void AssertNearSequence(
        IReadOnlyList<double> expected,
        IEnumerable<double> actual,
        string message)
    {
        double[] values = actual.ToArray();
        if (values.Length != expected.Count)
            throw new InvalidOperationException(message);
        for (int i = 0; i < values.Length; i++)
            AssertNear(expected[i], values[i], message);
    }

    private static void AssertSequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(message);
    }

    private sealed class LevelSnapshot
    {
        private LevelSnapshot(
            double[] angles,
            System.Numerics.Vector2[] positions,
            WorldRect bounds,
            string[] actions)
        {
            Angles = angles;
            Positions = positions;
            Bounds = bounds;
            Actions = actions;
        }

        private double[] Angles { get; }
        private System.Numerics.Vector2[] Positions { get; }
        private WorldRect Bounds { get; }
        private string[] Actions { get; }

        public static LevelSnapshot Create(LevelDocument level) => new(
            level.Angles.ToArray(),
            level.Positions.ToArray(),
            level.Bounds,
            level.ActionStore.Actions.Select(action => string.Join(
                "|",
                action.Floor,
                action.SourceIndex,
                action.EventType,
                action.Active,
                action.AngleOffset,
                action.Duration,
                action.PropertyOverrides?.ToJsonString())).ToArray());

        public bool Matches(LevelSnapshot other) =>
            Bounds.Equals(other.Bounds) &&
            Angles.SequenceEqual(other.Angles) &&
            Positions.SequenceEqual(other.Positions) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal);
    }
}
