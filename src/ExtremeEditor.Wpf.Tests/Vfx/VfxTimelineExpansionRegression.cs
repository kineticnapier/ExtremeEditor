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
        VerifyExtremeBeatRepeatRejected();
        VerifyNonFiniteRepeatInputsRejected();
        VerifyNonFiniteSourceEventValuesRejected();
        VerifyHugeFloorRepeatStopsAtChartEnd();
        VerifyBeatRepeatExpansionBudgetBoundary();
        VerifyBeatRepeatBudgetAccumulatesAcrossSources();
        VerifyUnresolvedCrossFloorRepeatBudget();
    }

    private static void VerifyExtremeBeatRepeatRejected()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "huge"),
            CreateRepeat(1, 1, "Beat", "huge", active: true,
                repetitions: int.MaxValue, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);

        try
        {
            _ = VfxTimelineBuilder.Build(level);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("RepeatEvents", StringComparison.Ordinal) &&
            ex.Message.Contains("source index 1", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "An excessive Beat RepeatEvents must fail before allocating occurrences.");
    }

    private static void VerifyNonFiniteRepeatInputsRejected()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        foreach (string interval in new[] { "Infinity", "NaN" })
        {
            LevelAction repeat = CreateRepeat(1, 1, "Beat", "bad-interval",
                active: true, repetitions: 1, interval: null, floorCount: null,
                executeOnCurrentFloor: false);
            repeat.PropertyOverrides!["interval"] = interval;
            level.ReplaceActions([CreateVfx(1, 0, 0, "bad-interval"), repeat]);

            try
            {
                _ = VfxTimelineBuilder.Build(level);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("source index 1", StringComparison.Ordinal) &&
                ex.Message.Contains("non-finite interval", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"RepeatEvents must reject the non-finite interval '{interval}'.");
        }

        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "overflow"),
            CreateRepeat(1, 1, "Beat", "overflow", active: true,
                repetitions: 1, interval: double.MaxValue, floorCount: null,
                executeOnCurrentFloor: false)
        ]);
        try
        {
            _ = VfxTimelineBuilder.Build(level);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("source index 1", StringComparison.Ordinal) &&
            ex.Message.Contains("non-finite angle offset", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "RepeatEvents must reject finite intervals whose angle multiplication overflows.");
    }

    private static void VerifyNonFiniteSourceEventValuesRejected()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        foreach ((double angle, double duration, string expected) in new[]
        {
            (double.PositiveInfinity, 1.25, "non-finite angle offset"),
            (0.0, double.NaN, "non-finite duration")
        })
        {
            level.ReplaceActions(
            [
                new LevelAction(1, "MoveDecorations", true, null, null, null, null)
                {
                    SourceIndex = 0,
                    AngleOffset = angle,
                    Duration = duration
                }
            ]);
            RequireRejected(expected);
        }

        // Both source fields are finite, but converting an enormous duration
        // at a very low pitch can still overflow the computed duration.
        level.PitchPercent = 0.0001;
        level.ReplaceActions(
        [
            new LevelAction(1, "MoveDecorations", true, null, null, null, null)
            {
                SourceIndex = 0,
                AngleOffset = 0,
                Duration = double.MaxValue
            }
        ]);
        RequireRejected("non-finite time value");

        void RequireRejected(string expected)
        {
            try
            {
                _ = VfxTimelineBuilder.Build(level);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("source index 0", StringComparison.Ordinal) &&
                ex.Message.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"VFX timeline must reject {expected} before creating an occurrence.");
        }
    }

    private static void VerifyHugeFloorRepeatStopsAtChartEnd()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "bounded"),
            CreateRepeat(1, 1, "Floor", "bounded", active: true,
                repetitions: null, interval: null, floorCount: int.MaxValue,
                executeOnCurrentFloor: true)
        ]);

        VfxOccurrence[] occurrences = VfxTimelineBuilder.Build(level).Occurrences
            .Where(item => item.SourceIndex == 0).ToArray();
        AssertSequenceEqual([1, 2], occurrences.Select(item => item.Floor),
            "Floor RepeatEvents must stop at the last floor even with a huge count.");
    }

    private static void VerifyBeatRepeatExpansionBudgetBoundary()
    {
        // The exact limit must remain valid; the next occurrence must be rejected.
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "limit"),
            CreateRepeat(1, 1, "Beat", "limit", active: true,
                repetitions: 199_999, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);

        VfxTimeline accepted = VfxTimelineBuilder.Build(level);
        if (ForSource(accepted, 0).Length != 200_000)
            throw new InvalidOperationException("RepeatEvents must accept exactly 200,000 occurrences.");

        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "limit"),
            CreateRepeat(1, 1, "Beat", "limit", active: true,
                repetitions: 200_000, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);
        try
        {
            _ = VfxTimelineBuilder.Build(level);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("200001", StringComparison.Ordinal) &&
            ex.Message.Contains("200000", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("RepeatEvents must reject 200,001 occurrences.");
    }

    private static void VerifyBeatRepeatBudgetAccumulatesAcrossSources()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "first"),
            CreateRepeat(1, 1, "Beat", "first", active: true,
                repetitions: 100_000, interval: 1, floorCount: null,
                executeOnCurrentFloor: false),
            CreateVfx(1, 2, 0, "second"),
            CreateRepeat(1, 3, "Beat", "second", active: true,
                repetitions: 100_000, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);

        try
        {
            _ = VfxTimelineBuilder.Build(level);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("source index 3", StringComparison.Ordinal) &&
            ex.Message.Contains("100001", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "RepeatEvents must apply the expansion budget across all matching sources.");
    }

    private static void VerifyUnresolvedCrossFloorRepeatBudget()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "orphan"),
            CreateVfx(1, 2, 0, "orphan extra"),
            CreateRepeat(2, 1, "Beat", "orphan", active: true,
                repetitions: 1, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);

        // An unused cross-floor RepeatEvents retains one unresolved
        // association. Its exact tag must not match a multi-tag string.
        VfxOccurrence[] unresolved = VfxTimelineBuilder.Build(level).Occurrences
            .Where(item => item.RepeatSourceIndex == 1).ToArray();
        if (unresolved.Length != 1 ||
            unresolved[0].SourceIndex != 0 ||
            unresolved[0].RepeatIteration != -1 ||
            unresolved[0].RepeatPlacementIsResolved)
        {
            throw new InvalidOperationException(
                "Cross-floor RepeatEvents must preserve exact-tag unresolved association semantics.");
        }

        level.ReplaceActions(
        [
            CreateVfx(1, 0, 0, "bulk"),
            CreateRepeat(1, 1, "Beat", "bulk", active: true,
                repetitions: 199_999, interval: 1, floorCount: null,
                executeOnCurrentFloor: false),
            CreateVfx(1, 2, 0, "orphan"),
            CreateRepeat(2, 3, "Beat", "orphan", active: true,
                repetitions: 1, interval: 1, floorCount: null,
                executeOnCurrentFloor: false)
        ]);

        try
        {
            _ = VfxTimelineBuilder.Build(level);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("source index 3", StringComparison.Ordinal) &&
            ex.Message.Contains("200000", StringComparison.Ordinal) &&
            ex.Message.Contains("unresolved cross-floor association", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "Unresolved cross-floor RepeatEvents must share the executable expansion budget.");
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
        // Stock bpm * floor.speed must follow the level bpm and SetSpeed.
        double floorSpeed = floor.Bpm / level.InitialBpm;
        if (Math.Abs((floor.StockBpm ?? 0) - level.InitialBpm * floorSpeed) > floor.Bpm * 1e-6)
            throw new InvalidOperationException("Stock VFX bpm does not use the level bpm and floor.speed.");
        double expected = StockVfxStartTimeExpectation.For(floor, occurrence.AngleOffset.GetValueOrDefault());
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
