using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsInterpolationRegression
{
    private const double Tolerance = 1e-9;
    private const string MissingInterpolationMessage =
        "MoveDecorations linear interpolation does not match game timing semantics.";

    public static void Run()
    {
        VerifyLinearTimingAndProperties();
        VerifyOverlappingTweenCompletionSemantics();
        VerifyRepeatOccurrenceTiming();
    }

    private static void VerifyLinearTimingAndProperties()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.InitialBpm = 120;
        level.PitchPercent = 50;
        LevelDecoration decoration = CreateDecoration(
            sourceIndex: 0,
            tag: "linear",
            position: (0, 0),
            rotation: 0,
            scale: (100, 100),
            opacity: 100);
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            new LevelAction(1, "SetSpeed", true, "Bpm", 240, null, null)
            {
                SourceIndex = 0,
                PropertyOverrides = new JsonObject
                {
                    ["floor"] = 1,
                    ["eventType"] = "SetSpeed",
                    ["active"] = true,
                    ["speedType"] = "Bpm",
                    ["beatsPerMinute"] = 240
                }
            },
            CreateMove(
                floor: 1,
                sourceIndex: 1,
                tag: "linear",
                angleOffset: 180,
                duration: 2,
                positionOffset: (20, 40),
                rotationOffset: 80,
                scale: (200, 50),
                opacity: 20)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence occurrence = FindOccurrence(timeline, 1, repeated: false);
        TimingMap timing = TimingMapBuilder.Build(level);
        FloorTiming floor = timing.Floors[occurrence.Floor];
        double expectedStart = floor.EntryTime + 60.0 / floor.Bpm;
        AssertNear(expectedStart, occurrence.StartTime,
            "MoveDecorations fixture did not resolve the expected #15 StartTime.");

        // Stock ffxMoveDecorationsPlus.Decode uses event duration * crotchet,
        // where ApplyEvent supplies crotchet = 60 / (bpm * pitch * floor.speed).
        double pitch = level.PitchPercent * 0.01;
        double durationSeconds = 2 * 60.0 / (floor.Bpm * pitch);
        InputSnapshot before = InputSnapshot.Create(level, timeline);

        AssertState(
            DecorationState.Evaluate(decoration, timeline, occurrence.StartTime - 0.001),
            (0, 0), 0, (100, 100), 100,
            "MoveDecorations changed state before a duration tween started.");
        AssertState(
            DecorationState.Evaluate(decoration, timeline, occurrence.StartTime),
            (0, 0), 0, (100, 100), 100,
            "MoveDecorations duration tween did not begin from the current decoration state.");
        AssertState(
            DecorationState.Evaluate(decoration, timeline, occurrence.StartTime + durationSeconds * 0.5),
            (10, 20), 40, (150, 75), 60,
            MissingInterpolationMessage);
        AssertState(
            DecorationState.Evaluate(decoration, timeline, occurrence.StartTime + durationSeconds),
            (20, 40), 80, (200, 50), 20,
            "MoveDecorations duration tween did not reach its target at the stock end time.");

        DecorationState first = DecorationState.Evaluate(
            decoration,
            timeline,
            occurrence.StartTime + durationSeconds * 0.5);
        DecorationState second = DecorationState.Evaluate(
            decoration,
            timeline,
            occurrence.StartTime + durationSeconds * 0.5);
        if (first != second)
            throw new InvalidOperationException("MoveDecorations interpolation is not deterministic.");
        if (!before.Matches(InputSnapshot.Create(level, timeline)))
            throw new InvalidOperationException("MoveDecorations interpolation mutated its input model.");
    }

    private static void VerifyOverlappingTweenCompletionSemantics()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration(0, "overlap", (0, 0), 0, (100, 100), 100);
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "overlap", 180, 4, rotationOffset: 40),
            CreateMove(0, 1, "overlap", 360, 2, rotationOffset: 80)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence moveA = FindOccurrence(timeline, 0, repeated: false);
        VfxOccurrence moveB = FindOccurrence(timeline, 1, repeated: false);

        AssertState(
            DecorationState.Evaluate(decoration, timeline, moveB.StartTime - 0.001),
            (0, 0), 9.99, (100, 100), 100,
            "The first overlapping MoveDecorations tween was not evaluated before interruption.",
            tolerance: 0.02);
        AssertState(
            DecorationState.Evaluate(decoration, timeline, moveB.StartTime),
            (0, 0), 40, (100, 100), 100,
            "Starting a replacement tween did not complete the prior same-property tween.");
        AssertState(
            DecorationState.Evaluate(decoration, timeline, moveB.StartTime + 1),
            (0, 0), 60, (100, 100), 100,
            "Replacement tween did not start from the completed prior target.");

        _ = moveA;
    }

    private static void VerifyRepeatOccurrenceTiming()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration(0, "repeat", (0, 0), 0, (100, 100), 0);
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "repeat", 0, 1, opacity: 100, eventTag: "repeat-move"),
            CreateRepeat(0, 1, "repeat-move", interval: 2),
            CreateMove(0, 2, "repeat", 270, 0, opacity: 0)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence repeated = FindOccurrence(timeline, 0, repeated: true);
        AssertState(
            DecorationState.Evaluate(decoration, timeline, repeated.StartTime + 0.5),
            (0, 0), 0, (100, 100), 50,
            "A RepeatEvents-derived MoveDecorations occurrence did not use its own StartTime.");
    }

    private static LevelDecoration CreateDecoration(
        int sourceIndex,
        string tag,
        (double X, double Y) position,
        double rotation,
        (double X, double Y) scale,
        double opacity) =>
        new(0, "AddDecoration")
        {
            SourceIndex = sourceIndex,
            Properties = new JsonObject
            {
                ["tag"] = tag,
                ["position"] = new JsonArray(position.X, position.Y),
                ["rotation"] = rotation,
                ["scale"] = new JsonArray(scale.X, scale.Y),
                ["opacity"] = opacity,
                ["futureData"] = new JsonObject { ["keep"] = true }
            }
        };

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        double duration,
        (double X, double Y)? positionOffset = null,
        double? rotationOffset = null,
        (double X, double Y)? scale = null,
        double? opacity = null,
        string? eventTag = null)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = angleOffset,
            ["duration"] = duration,
            ["ease"] = "Linear",
            ["tag"] = tag
        };
        if (positionOffset is { } position)
            properties["positionOffset"] = new JsonArray(position.X, position.Y);
        if (rotationOffset is double rotation)
            properties["rotationOffset"] = rotation;
        if (scale is { } scaleValue)
            properties["scale"] = new JsonArray(scaleValue.X, scaleValue.Y);
        if (opacity is double opacityValue)
            properties["opacity"] = opacityValue;
        if (eventTag is not null)
            properties["eventTag"] = eventTag;

        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = properties
        };
    }

    private static LevelAction CreateRepeat(
        int floor,
        int sourceIndex,
        string tag,
        double interval) =>
        new(floor, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "RepeatEvents",
                ["active"] = true,
                ["repeatType"] = "Beat",
                ["tag"] = tag,
                ["repetitions"] = 1,
                ["interval"] = interval,
                ["executeOnCurrentFloor"] = false
            }
        };

    private static VfxOccurrence FindOccurrence(
        VfxTimeline timeline,
        int sourceIndex,
        bool repeated) =>
        timeline.Occurrences.Single(occurrence =>
            occurrence.SourceIndex == sourceIndex && occurrence.IsRepeated == repeated);

    private static void AssertState(
        DecorationState actual,
        (double X, double Y) position,
        double rotation,
        (double X, double Y) scale,
        double opacity,
        string message,
        double tolerance = Tolerance)
    {
        if (!Near(position.X, actual.PositionX, tolerance) ||
            !Near(position.Y, actual.PositionY, tolerance) ||
            !Near(rotation, actual.Rotation, tolerance) ||
            !Near(scale.X, actual.ScaleX, tolerance) ||
            !Near(scale.Y, actual.ScaleY, tolerance) ||
            !Near(opacity, actual.Opacity, tolerance))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertNear(double expected, double actual, string message)
    {
        if (!Near(expected, actual, Tolerance))
            throw new InvalidOperationException(message);
    }

    private static bool Near(double expected, double actual, double tolerance) =>
        Math.Abs(expected - actual) <= tolerance;

    private sealed record InputSnapshot(string[] Decorations, string[] Actions, string[] Occurrences)
    {
        public static InputSnapshot Create(LevelDocument level, VfxTimeline timeline) => new(
            level.Decorations.Select(decoration => decoration.Properties.ToJsonString()).ToArray(),
            level.ActionStore.Actions.Select(action =>
                action.PropertyOverrides?.ToJsonString() ?? string.Empty).ToArray(),
            timeline.Occurrences.Select(occurrence => string.Join(
                "|",
                occurrence.Floor,
                occurrence.SourceIndex,
                occurrence.StartTime,
                occurrence.Duration,
                occurrence.RepeatIteration,
                occurrence.SourceEvent.PropertyOverrides?.ToJsonString())).ToArray());

        public bool Matches(InputSnapshot other) =>
            Decorations.SequenceEqual(other.Decorations, StringComparer.Ordinal) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal) &&
            Occurrences.SequenceEqual(other.Occurrences, StringComparer.Ordinal);
    }
}
