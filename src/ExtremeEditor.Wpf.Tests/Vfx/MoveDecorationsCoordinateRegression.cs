using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsCoordinateRegression
{
    private const double Tolerance = 1e-9;
    private const string MissingCoordinateSemantics =
        "MoveDecorations LastPosition does not match game semantics.";

    public static void Run()
    {
        VerifyGlobalAndLastPositionBases();
        VerifyLastPositionOverlapOrdering();
    }

    private static void VerifyGlobalAndLastPositionBases()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration("coordinate", (10, 20));
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "coordinate", 0, 0, "Global", (5, -2)),
            CreateMove(0, 1, "coordinate", 180, 0, "LastPosition", (2, 3))
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence global = FindOccurrence(timeline, 0);
        VfxOccurrence lastPosition = FindOccurrence(timeline, 1);
        InputSnapshot before = InputSnapshot.Create(level, timeline);

        AssertPosition(
            DecorationState.Evaluate(decoration, timeline, global.StartTime),
            (15, 18),
            "MoveDecorations Global did not use the AddDecoration placement base.");
        AssertPosition(
            DecorationState.Evaluate(decoration, timeline, lastPosition.StartTime),
            (17, 21),
            MissingCoordinateSemantics);

        VerifyUnchanged(level, timeline, before);
    }

    private static void VerifyLastPositionOverlapOrdering()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration("overlap-position", (0, 0));
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "overlap-position", 0, 10, "Global", (100, 0)),
            CreateMove(0, 1, "overlap-position", 900, 10, "LastPosition", (20, 0))
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence replacement = FindOccurrence(timeline, 1);
        InputSnapshot before = InputSnapshot.Create(level, timeline);

        // Stock StartEffect captures LastPosition from pivotPosVec before it
        // kills the old position tween with complete:true. At t=5 that makes
        // the new target 50 + 20 = 70, while the new tween starts at the old
        // completed target 100.
        AssertPosition(
            DecorationState.Evaluate(decoration, timeline, replacement.StartTime),
            (100, 0),
            "LastPosition replacement did not preserve Kill(complete:true) start semantics.");
        AssertPosition(
            DecorationState.Evaluate(decoration, timeline, replacement.StartTime + 5),
            (85, 0),
            "MoveDecorations LastPosition did not capture its base before completing the old tween.");

        DecorationState first = DecorationState.Evaluate(
            decoration, timeline, replacement.StartTime + 5);
        DecorationState second = DecorationState.Evaluate(
            decoration, timeline, replacement.StartTime + 5);
        if (first != second)
            throw new InvalidOperationException("MoveDecorations coordinate evaluation is not deterministic.");

        VerifyUnchanged(level, timeline, before);
    }

    private static LevelDecoration CreateDecoration(string tag, (double X, double Y) position) =>
        new(0, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["tag"] = tag,
                ["relativeTo"] = "Global",
                ["position"] = new JsonArray(position.X, position.Y),
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100,
                ["futureData"] = new JsonObject { ["keep"] = true }
            }
        };

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        double duration,
        string relativeTo,
        (double X, double Y) positionOffset)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = angleOffset,
            ["duration"] = duration,
            ["ease"] = "Linear",
            ["tag"] = tag,
            ["relativeTo"] = relativeTo,
            ["positionOffset"] = new JsonArray(positionOffset.X, positionOffset.Y)
        };
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = properties
        };
    }

    private static VfxOccurrence FindOccurrence(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(occurrence =>
            occurrence.SourceIndex == sourceIndex && !occurrence.IsRepeated);

    private static void AssertPosition(
        DecorationState actual,
        (double X, double Y) expected,
        string message)
    {
        if (Math.Abs(actual.PositionX - expected.X) > Tolerance ||
            Math.Abs(actual.PositionY - expected.Y) > Tolerance)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void VerifyUnchanged(
        LevelDocument level,
        VfxTimeline timeline,
        InputSnapshot before)
    {
        if (!before.Matches(InputSnapshot.Create(level, timeline)))
            throw new InvalidOperationException("MoveDecorations coordinate evaluation mutated its input model.");
    }

    private sealed record InputSnapshot(string[] Decorations, string[] Actions, string[] Occurrences)
    {
        public static InputSnapshot Create(LevelDocument level, VfxTimeline timeline) => new(
            level.Decorations.Select(item => item.Properties.ToJsonString()).ToArray(),
            level.ActionStore.Actions.Select(item =>
                item.PropertyOverrides?.ToJsonString() ?? string.Empty).ToArray(),
            timeline.Occurrences.Select(item => string.Join(
                "|", item.Floor, item.SourceIndex, item.StartTime, item.Duration,
                item.RepeatIteration, item.SourceEvent.PropertyOverrides?.ToJsonString())).ToArray());

        public bool Matches(InputSnapshot other) =>
            Decorations.SequenceEqual(other.Decorations, StringComparer.Ordinal) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal) &&
            Occurrences.SequenceEqual(other.Occurrences, StringComparer.Ordinal);
    }
}
