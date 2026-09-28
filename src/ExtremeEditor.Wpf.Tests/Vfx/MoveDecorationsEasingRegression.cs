using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsEasingRegression
{
    private const double Tolerance = 0.00001;

    public static void Run()
    {
        VerifyDefaultEaseIsLinear();
        VerifyRepresentativeEases();
        VerifyEaseIsSharedByAllTweenedProperties();
        VerifyOverlapUsesNewEventEase();
        VerifySameTimeOrderAndKillSemantics();
        VerifyRepeatOccurrenceKeepsSourceEase();
    }

    private static void VerifyDefaultEaseIsLinear()
    {
        Fixture fixture = CreateSingleMoveFixture(ease: null, duration: 4);
        DecorationState state = EvaluateAtProgress(fixture, 0.25);
        AssertNear(25, state.Rotation,
            "MoveDecorations with no ease did not use the game default Linear ease.");
    }

    private static void VerifyRepresentativeEases()
    {
        string[] eases =
        [
            "Linear",
            "InSine",
            "OutSine",
            "InOutSine",
            "InQuad",
            "OutQuad",
            "InOutQuad",
            "InCubic",
            "OutCubic",
            "InOutCubic"
        ];

        foreach (string ease in eases)
        {
            Fixture fixture = CreateSingleMoveFixture(ease, duration: 4);
            foreach (float progress in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                DecorationState state = EvaluateAtProgress(fixture, progress);
                double expected = 100 * EvaluateDotweenProgress(ease, progress);
                AssertNear(expected, state.Rotation,
                    $"MoveDecorations {ease} easing does not match DOTween semantics.");
            }
        }
    }

    private static void VerifyEaseIsSharedByAllTweenedProperties()
    {
        Fixture fixture = CreateSingleMoveFixture(
            ease: "InQuad",
            duration: 4,
            includeAllProperties: true);
        DecorationState state = EvaluateAtProgress(fixture, 0.5);

        AssertNear(25, state.PositionX,
            "MoveDecorations position did not use the event ease.");
        AssertNear(25, state.PositionY,
            "MoveDecorations position did not use the event ease.");
        AssertNear(25, state.Rotation,
            "MoveDecorations rotation did not use the event ease.");
        AssertNear(25, state.ScaleX,
            "MoveDecorations scale did not use the event ease.");
        AssertNear(25, state.ScaleY,
            "MoveDecorations scale did not use the event ease.");
        AssertNear(25, state.Opacity,
            "MoveDecorations opacity did not use the event ease.");
    }

    private static void VerifyOverlapUsesNewEventEase()
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("overlap");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "overlap", angleOffset: 180, duration: 4,
                ease: "InQuad", rotation: 100),
            CreateMove(0, 1, "overlap", angleOffset: 360, duration: 2,
                ease: "OutQuad", rotation: 200)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence moveB = FindOccurrence(timeline, 1, repeated: false);
        DecorationState atStart = DecorationState.Evaluate(decoration, timeline, moveB.StartTime);
        AssertNear(100, atStart.Rotation,
            "An eased tween was not completed to its target before replacement.");

        DecorationState halfway = DecorationState.Evaluate(
            decoration,
            timeline,
            moveB.StartTime + moveB.DurationSeconds!.Value * 0.5);
        AssertNear(175, halfway.Rotation,
            "Replacement MoveDecorations did not use its own OutQuad ease.");
    }

    private static void VerifyRepeatOccurrenceKeepsSourceEase()
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("repeat");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "repeat", angleOffset: 0, duration: 1,
                ease: "InQuad", rotation: 100, eventTag: "repeat-move"),
            CreateRepeat(0, 1, "repeat-move", interval: 2),
            CreateMove(0, 2, "repeat", angleOffset: 270, duration: 0,
                ease: "Linear", rotation: 0)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence repeated = FindOccurrence(timeline, 0, repeated: true);
        DecorationState halfway = DecorationState.Evaluate(
            decoration,
            timeline,
            repeated.StartTime + repeated.DurationSeconds!.Value * 0.5);
        AssertNear(25, halfway.Rotation,
            "RepeatEvents-derived MoveDecorations did not retain the source event ease.");
    }

    private static void VerifySameTimeOrderAndKillSemantics()
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("same-time");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "same-time", angleOffset: 180, duration: 4,
                ease: "InQuad", rotation: 100),
            CreateMove(0, 1, "same-time", angleOffset: 180, duration: 4,
                ease: "OutQuad", rotation: 200)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence second = FindOccurrence(timeline, 1, repeated: false);
        DecorationState atStart = DecorationState.Evaluate(decoration, timeline, second.StartTime);
        AssertNear(100, atStart.Rotation,
            "Same-time MoveDecorations did not complete the earlier occurrence in timeline order.");

        DecorationState halfway = DecorationState.Evaluate(
            decoration,
            timeline,
            second.StartTime + second.DurationSeconds!.Value * 0.5);
        AssertNear(175, halfway.Rotation,
            "Same-time replacement MoveDecorations did not use the later occurrence ease.");
    }

    private static Fixture CreateSingleMoveFixture(
        string? ease,
        double duration,
        bool includeAllProperties = false)
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("target");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(
                0,
                0,
                "target",
                angleOffset: 180,
                duration,
                ease,
                position: includeAllProperties ? (100, 100) : null,
                rotation: 100,
                scale: includeAllProperties ? (100, 100) : null,
                opacity: includeAllProperties ? 100 : null)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        return new Fixture(
            level,
            decoration,
            timeline,
            FindOccurrence(timeline, 0, repeated: false));
    }

    private static DecorationState EvaluateAtProgress(Fixture fixture, double progress)
    {
        string before = Snapshot(fixture.Level, fixture.Timeline);
        DecorationState state = DecorationState.Evaluate(
            fixture.Decoration,
            fixture.Timeline,
            fixture.Occurrence.StartTime + fixture.Occurrence.DurationSeconds!.Value * progress);
        if (!string.Equals(before, Snapshot(fixture.Level, fixture.Timeline), StringComparison.Ordinal))
            throw new InvalidOperationException("MoveDecorations easing evaluation mutated its input model.");
        return state;
    }

    private static LevelDocument CreateLevel()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        return level;
    }

    private static LevelDecoration CreateDecoration(string tag) =>
        new(0, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["tag"] = tag,
                ["position"] = new JsonArray(0, 0),
                ["rotation"] = 0,
                ["scale"] = new JsonArray(0, 0),
                ["opacity"] = 0,
                ["futureData"] = new JsonObject { ["keep"] = true }
            }
        };

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        double duration,
        string? ease,
        (double X, double Y)? position = null,
        double? rotation = null,
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
            ["tag"] = tag
        };
        if (ease is not null)
            properties["ease"] = ease;
        if (position is { } positionValue)
            properties["positionOffset"] = new JsonArray(positionValue.X, positionValue.Y);
        if (rotation is double rotationValue)
            properties["rotationOffset"] = rotationValue;
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

    private static float EvaluateDotweenProgress(string ease, float progress) => ease switch
    {
        "Linear" => progress,
        "InSine" => 0f - MathF.Cos(progress * (MathF.PI / 2f)) + 1f,
        "OutSine" => MathF.Sin(progress * (MathF.PI / 2f)),
        "InOutSine" => -0.5f * (MathF.Cos(MathF.PI * progress) - 1f),
        "InQuad" => progress * progress,
        "OutQuad" => -progress * (progress - 2f),
        "InOutQuad" => EvaluateInOutQuad(progress),
        "InCubic" => progress * progress * progress,
        "OutCubic" => EvaluateOutCubic(progress),
        "InOutCubic" => EvaluateInOutCubic(progress),
        _ => throw new InvalidOperationException($"No DOTween oracle exists for {ease}.")
    };

    private static float EvaluateInOutQuad(float progress)
    {
        float time = progress * 2f;
        if (time < 1f)
            return 0.5f * time * time;
        time--;
        return -0.5f * (time * (time - 2f) - 1f);
    }

    private static float EvaluateOutCubic(float progress)
    {
        float time = progress - 1f;
        return time * time * time + 1f;
    }

    private static float EvaluateInOutCubic(float progress)
    {
        float time = progress * 2f;
        if (time < 1f)
            return 0.5f * time * time * time;
        time -= 2f;
        return 0.5f * (time * time * time + 2f);
    }

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > Tolerance)
            throw new InvalidOperationException(message);
    }

    private static string Snapshot(LevelDocument level, VfxTimeline timeline) => string.Join(
        "\n",
        level.Decorations.Select(decoration => decoration.Properties.ToJsonString())
            .Concat(level.ActionStore.Actions.Select(action =>
                action.PropertyOverrides?.ToJsonString() ?? string.Empty))
            .Concat(timeline.Occurrences.Select(occurrence => string.Join(
                "|",
                occurrence.Floor,
                occurrence.SourceIndex,
                occurrence.StartTime,
                occurrence.DurationSeconds,
                occurrence.Ease,
                occurrence.RepeatIteration,
                occurrence.SourceEvent.PropertyOverrides?.ToJsonString()))));

    private sealed record Fixture(
        LevelDocument Level,
        LevelDecoration Decoration,
        VfxTimeline Timeline,
        VfxOccurrence Occurrence);
}
