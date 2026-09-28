using System.Numerics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class VfxTimelineEdgeRegression
{
    private const double Tolerance = 1e-9;

    public static void Run()
    {
        LevelDocument level = CreateRepeatFixture();
        TimingMap timing = TimingMapBuilder.Build(level);
        InputSnapshot before = InputSnapshot.Create(level);

        VfxTimeline first = VfxTimelineBuilder.Build(level);
        VfxTimeline second = VfxTimelineBuilder.Build(level);

        VerifyZeroIntervalBeatUsesFloorBranch(first, timing, level.FloorCount);
        VerifyMovingFloorUsesTargetTiming(first, timing);
        VerifyFixedFloorUsesSourceTiming(first, timing);
        VerifyDuplicateRepeatUsesLaterDefinition(first);
        VerifyEventTagOrderWinsOverRepeatRegistrationOrder(first);
        VerifyRepeatTagTokens(first);
        VerifyUnmatchedEventOccursOnce(first);
        VerifyTieOrdering();
        VerifyDeterministic(first, second);
        VerifyNonDestructive(level, before);
    }

    private static LevelDocument CreateRepeatFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(10);
        level.InitialBpm = 120;
        level.PitchPercent = 61;

        var speedUp = new LevelAction(3, "SetSpeed", true, "Multiplier", null, 2, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 0,
            SpeedRatio = 2
        };
        var speedDown = new LevelAction(6, "SetSpeed", true, "Multiplier", null, 0.5, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 18,
            SpeedRatio = 0.5
        };

        level.ReplaceActions(
        [
            CreateVfx(1, 1, 11, "dup"),
            CreateRepeat(1, 2, "Beat", "dup", repetitions: 1, interval: 0.25),
            CreateRepeat(1, 3, "Beat", "dup", repetitions: 2, interval: 1),

            CreateVfx(2, 4, 20, "moving"),
            CreateRepeat(2, 5, "Floor", "moving", floorCount: 2, executeOnCurrentFloor: true),
            CreateVfx(2, 6, 25, "fixed"),
            CreateRepeat(2, 7, "Floor", "fixed", floorCount: 2, executeOnCurrentFloor: false),

            speedUp,

            CreateVfx(4, 8, 30, "A B"),
            CreateVfx(4, 9, 40, "B A"),
            CreateRepeat(4, 10, "Beat", "B", repetitions: 1, interval: 2),
            CreateRepeat(4, 11, "Beat", "A", repetitions: 2, interval: 0.5),

            CreateVfx(5, 12, 5, "splitA"),
            CreateVfx(5, 13, 7, "splitB"),
            CreateRepeat(5, 14, "Beat", "splitA splitB", repetitions: 1, interval: 0.25),

            speedDown,
            CreateVfx(7, 17, 3, "unmatched"),
            CreateRepeat(7, 19, "Beat", "other", repetitions: 3, interval: 1),

            CreateVfx(8, 15, 13, "zero-interval"),
            CreateRepeat(
                8,
                16,
                "Beat",
                "zero-interval",
                repetitions: 4,
                interval: 0,
                executeOnCurrentFloor: false)
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
            Duration = 0.75,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "MoveDecorations",
                ["active"] = true,
                ["angleOffset"] = angleOffset,
                ["duration"] = 0.75,
                ["ease"] = "Linear",
                ["eventTag"] = eventTag
            }
        };
    }

    private static LevelAction CreateRepeat(
        int floor,
        int sourceIndex,
        string repeatType,
        string tag,
        int? repetitions = null,
        double? interval = null,
        int? floorCount = null,
        bool executeOnCurrentFloor = false)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "RepeatEvents",
            ["active"] = true,
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

        return new LevelAction(floor, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = properties
        };
    }

    private static void VerifyZeroIntervalBeatUsesFloorBranch(
        VfxTimeline timeline,
        TimingMap timing,
        int floorCount)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 15);
        int expectedCount = Math.Min(4 + 1, floorCount - 8);
        if (occurrences.Length != expectedCount)
        {
            throw new InvalidOperationException(
                "Beat Repeat interval=0 does not follow game floor-style expansion semantics.");
        }

        double[] entryBeats = BuildEntryBeats(timing);
        double[] expectedAngles = Enumerable.Range(0, expectedCount)
            .Select(i => 13 + (entryBeats[8 + i] - entryBeats[8]) * 180)
            .ToArray();
        AssertSequenceEqual(Enumerable.Repeat(8, expectedCount), occurrences.Select(item => item.Floor),
            "Zero-interval Beat Repeat changed the effective floor when executeOnCurrentFloor=false.");
        AssertNearSequence(expectedAngles, occurrences.Select(item => item.AngleOffset ?? 0),
            "Zero-interval Beat Repeat did not add its floor entryBeat offset to source angleOffset.");
    }

    private static void VerifyMovingFloorUsesTargetTiming(VfxTimeline timeline, TimingMap timing)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 4);
        AssertSequenceEqual([2, 3, 4], occurrences.Select(item => item.Floor),
            "executeOnCurrentFloor=true did not move occurrences to sourceFloor+i.");
        AssertNearSequence([20, 20, 20], occurrences.Select(item => item.AngleOffset ?? 0),
            "executeOnCurrentFloor=true did not preserve the original angleOffset.");

        foreach (VfxOccurrence occurrence in occurrences)
        {
            FloorTiming target = timing.Floors[occurrence.Floor];
            double expected = target.EntryTime + 20.0 / 180.0 * (60.0 / target.Bpm);
            AssertNear(expected, occurrence.StartTime,
                "Moving Floor Repeat StartTime did not use target floor entryTime and speed.");
        }
    }

    private static void VerifyFixedFloorUsesSourceTiming(VfxTimeline timeline, TimingMap timing)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 6);
        AssertSequenceEqual([2, 2, 2], occurrences.Select(item => item.Floor),
            "executeOnCurrentFloor=false changed the effective source floor.");

        double[] entryBeats = BuildEntryBeats(timing);
        FloorTiming sourceTiming = timing.Floors[2];
        for (int i = 0; i < occurrences.Length; i++)
        {
            double expectedAngle = 25 + (entryBeats[2 + i] - entryBeats[2]) * 180;
            double expectedTime = sourceTiming.EntryTime +
                                  expectedAngle / 180.0 * (60.0 / sourceTiming.Bpm);
            AssertNear(expectedAngle, occurrences[i].AngleOffset ?? 0,
                "Fixed Floor Repeat overwrote rather than composed source angleOffset.");
            AssertNear(expectedTime, occurrences[i].StartTime,
                "Fixed Floor Repeat StartTime did not retain source floor entryTime and speed.");
        }
    }

    private static void VerifyDuplicateRepeatUsesLaterDefinition(VfxTimeline timeline)
    {
        VfxOccurrence[] occurrences = ForSource(timeline, 1);
        if (occurrences.Length != 3 ||
            occurrences.Where(item => item.IsRepeated).Any(item => item.RepeatSourceIndex != 3))
        {
            throw new InvalidOperationException(
                "Duplicate same-floor/tag RepeatEvents did not use the later definition.");
        }
        AssertNearSequence([11, 191, 371], occurrences.Select(item => item.AngleOffset ?? 0),
            "Duplicate RepeatEvents used the earlier interval or repetition count.");
    }

    private static void VerifyEventTagOrderWinsOverRepeatRegistrationOrder(VfxTimeline timeline)
    {
        VfxOccurrence[] aThenB = ForSource(timeline, 8);
        VfxOccurrence[] bThenA = ForSource(timeline, 9);
        if (aThenB.Length != 3 ||
            aThenB.Where(item => item.IsRepeated).Any(item => item.RepeatSourceIndex != 11))
        {
            throw new InvalidOperationException("eventTag 'A B' did not select RepeatEvents A first.");
        }
        if (bThenA.Length != 2 ||
            bThenA.Where(item => item.IsRepeated).Any(item => item.RepeatSourceIndex != 10))
        {
            throw new InvalidOperationException("eventTag 'B A' did not select RepeatEvents B first.");
        }
    }

    private static void VerifyRepeatTagTokens(VfxTimeline timeline)
    {
        foreach (int sourceIndex in new[] { 12, 13 })
        {
            VfxOccurrence[] occurrences = ForSource(timeline, sourceIndex);
            if (occurrences.Length != 2 ||
                occurrences.Where(item => item.IsRepeated).Any(item => item.RepeatSourceIndex != 14))
            {
                throw new InvalidOperationException(
                    "Space-separated RepeatEvents tags are not independently addressable.");
            }
        }
    }

    private static void VerifyUnmatchedEventOccursOnce(VfxTimeline timeline)
    {
        if (ForSource(timeline, 17).Length != 1)
            throw new InvalidOperationException("An unmatched VFX event was repeated.");
    }

    private static void VerifyTieOrdering()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 120;
        TimingMap timing = TimingMapBuilder.Build(level);
        FloorTiming floor0 = timing.Floors[0];
        double tieAngle = (timing.Floors[1].EntryTime - floor0.EntryTime) /
                          (60.0 / floor0.Bpm) * 180.0;

        level.ReplaceActions(
        [
            CreateVfx(0, 100, tieAngle, "tie-floor-zero"),
            CreateVfx(1, 101, 0, "tie-first"),
            CreateVfx(1, 102, 0, "tie-second")
        ]);

        VfxOccurrence[] tied = VfxTimelineBuilder.Build(level).Occurrences
            .Where(item => item.SourceIndex is 100 or 101 or 102)
            .ToArray();
        AssertSequenceEqual([100, 101, 102], tied.Select(item => item.SourceIndex),
            "StartTime ties are not ordered by floor.seqID and stable source enumeration.");
    }

    private static void VerifyDeterministic(VfxTimeline first, VfxTimeline second)
    {
        string[] firstSignatures = first.Occurrences.Select(Signature).ToArray();
        string[] secondSignatures = second.Occurrences.Select(Signature).ToArray();
        if (!firstSignatures.SequenceEqual(secondSignatures, StringComparer.Ordinal))
            throw new InvalidOperationException("VFX edge occurrence enumeration is not deterministic.");
    }

    private static void VerifyNonDestructive(LevelDocument level, InputSnapshot before)
    {
        if (!before.Matches(InputSnapshot.Create(level)))
            throw new InvalidOperationException("VFX edge expansion mutated source document or action data.");
    }

    private static VfxOccurrence[] ForSource(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Where(item => item.SourceIndex == sourceIndex).ToArray();

    private static double[] BuildEntryBeats(TimingMap timing)
    {
        var entryBeats = new double[timing.Floors.Count];
        for (int floor = 1; floor < entryBeats.Length; floor++)
            entryBeats[floor] = entryBeats[floor - 1] + timing.Floors[floor - 1].AngleMoved / Math.PI;
        return entryBeats;
    }

    private static string Signature(VfxOccurrence occurrence) => string.Join(
        "|",
        occurrence.Floor,
        occurrence.SourceIndex,
        occurrence.AngleOffset,
        occurrence.StartTime,
        occurrence.IsRepeated,
        occurrence.RepeatSourceIndex);

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
        if (expected.Count != values.Length)
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

    private sealed class InputSnapshot
    {
        private InputSnapshot(double[] angles, Vector2[] positions, WorldRect bounds, string[] actions)
        {
            Angles = angles;
            Positions = positions;
            Bounds = bounds;
            Actions = actions;
        }

        private double[] Angles { get; }
        private Vector2[] Positions { get; }
        private WorldRect Bounds { get; }
        private string[] Actions { get; }

        public static InputSnapshot Create(LevelDocument level) => new(
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

        public bool Matches(InputSnapshot other) =>
            Bounds.Equals(other.Bounds) &&
            Angles.SequenceEqual(other.Angles) &&
            Positions.SequenceEqual(other.Positions) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal);
    }
}
