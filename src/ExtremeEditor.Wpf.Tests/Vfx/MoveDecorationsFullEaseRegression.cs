using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

// MoveDecorations runs on DOTween (ffxMoveDecorationsPlus -> DOTween.To(...).SetEase).
// DOTween.EaseManager.Evaluate supports every DG.Tweening.Ease value with the
// default overshoot/amplitude 1.70158 and period 0. A MoveDecorations event
// with any of them must be evaluated with the DOTween curve, and an ease name
// the game would not parse must still apply the event (Linear fallback).
internal static class MoveDecorationsFullEaseRegression
{
    private const double Overshoot = 1.70158;
    private const double OracleTolerance = 0.00005;
    private const double StateTolerance = 0.005;

    private static readonly string[] DotweenEases =
    [
        "Linear",
        "InSine", "OutSine", "InOutSine",
        "InQuad", "OutQuad", "InOutQuad",
        "InCubic", "OutCubic", "InOutCubic",
        "InQuart", "OutQuart", "InOutQuart",
        "InQuint", "OutQuint", "InOutQuint",
        "InExpo", "OutExpo", "InOutExpo",
        "InCirc", "OutCirc", "InOutCirc",
        "InElastic", "OutElastic", "InOutElastic",
        "InBack", "OutBack", "InOutBack",
        "InBounce", "OutBounce", "InOutBounce",
        "Flash", "InFlash", "OutFlash", "InOutFlash"
    ];

    public static void Run()
    {
        VerifyEvaluatorMatchesDotweenForEveryEase();
        VerifyIndependentGoldenValues();
        VerifyPhantigmaEasesMoveDecorations();
        VerifyBoundaries();
        VerifyOvershootIsNotClamped();
        VerifyZeroDurationAppliesTarget();
        VerifyEaseNameParsing();
        VerifySuccessiveEvents();
        VerifyRepeatEventsKeepEase();
        VerifySeekingBackwardIsStateless();
        VerifyDegenerateInputsStayFinite();
    }

    private static void VerifyEvaluatorMatchesDotweenForEveryEase()
    {
        double[] progresses = [0, 0.1, 0.25, 0.4, 0.5, 0.75, 0.9, 1];
        foreach (string ease in DotweenEases)
        {
            Require(DotweenEaseEvaluator.IsSupported(ease), $"{ease} is not a supported DOTween ease.");
            foreach (double duration in new[] { 4.0, 0.37 })
            {
                foreach (double progress in progresses)
                {
                    Require(DotweenEaseEvaluator.TryEvaluate(ease, progress * duration, duration, out float actual),
                        $"{ease} could not be evaluated at {progress}.");
                    double expected = Oracle(ease, progress);
                    Require(Math.Abs(expected - actual) <= OracleTolerance,
                        $"{ease} at {progress} (duration {duration}) = {actual}, DOTween gives {expected}.");
                }
            }
        }
    }

    private static void VerifyIndependentGoldenValues()
    {
        (string Ease, double Progress, double Expected)[] golden =
        [
            ("OutCirc", 0.5, 0.8660254037844386),
            ("InCirc", 0.5, 0.1339745962155614),
            ("InOutCirc", 0.25, 0.0669872981077807),
            ("OutElastic", 0.25, 0.7008443879120276),
            ("OutElastic", 0.5, 0.9783662595156456),
            ("OutBack", 0.5, 1.0876975),
            ("OutBack", 0.6, 1.09935168),
            ("OutExpo", 0.5, 0.96875)
        ];
        foreach ((string ease, double progress, double expected) in golden)
        {
            Require(DotweenEaseEvaluator.TryEvaluate(ease, progress * 2, 2, out float actual) &&
                    Math.Abs(expected - actual) <= OracleTolerance,
                $"{ease} at {progress} = {actual}, expected {expected}.");
        }
    }

    // The six eases Phantigma uses that the editor previously skipped, plus
    // the remaining families, applied to every tweened property.
    private static void VerifyPhantigmaEasesMoveDecorations()
    {
        foreach (string ease in DotweenEases)
        {
            Fixture fixture = CreateSingleMove(ease, duration: 4, allProperties: true);
            foreach (double progress in new[] { 0.25, 0.5, 0.75 })
            {
                DecorationState state = EvaluateAt(fixture, progress);
                double expected = 100 * Oracle(ease, progress);
                RequireNear(expected, state.Rotation, $"{ease} rotation at {progress}");
                RequireNear(expected, state.ScaleX, $"{ease} scale.x at {progress}");
                RequireNear(expected, state.ScaleY, $"{ease} scale.y at {progress}");
                RequireNear(expected, state.Opacity, $"{ease} opacity at {progress}");
                RequireNear(expected, state.PositionX, $"{ease} position.x at {progress}");
                RequireNear(expected, state.PositionY, $"{ease} position.y at {progress}");
            }
        }
    }

    private static void VerifyBoundaries()
    {
        foreach (string ease in new[] { "InCirc", "OutCirc", "InOutCirc", "OutElastic", "OutBack", "OutExpo", "InOutBounce" })
        {
            Fixture fixture = CreateSingleMove(ease, duration: 4);
            double start = fixture.Occurrence.StartTime;
            double end = start + fixture.Occurrence.DurationSeconds!.Value;
            RequireNear(0, DecorationState.Evaluate(fixture.Decoration, fixture.Timeline, start - 0.01).Rotation,
                $"{ease} changed the decoration before the event started");
            RequireNear(0, DecorationState.Evaluate(fixture.Decoration, fixture.Timeline, start).Rotation,
                $"{ease} at the event start");
            RequireNear(100, DecorationState.Evaluate(fixture.Decoration, fixture.Timeline, end).Rotation,
                $"{ease} at the event end");
            RequireNear(100, DecorationState.Evaluate(fixture.Decoration, fixture.Timeline, end + 10).Rotation,
                $"{ease} after the event ended");
        }
    }

    private static void VerifyOvershootIsNotClamped()
    {
        Fixture outBack = CreateSingleMove("OutBack", duration: 4);
        double peak = EvaluateAt(outBack, 0.6).Rotation;
        Require(peak > 109, $"OutBack overshoot was clamped: {peak}.");

        Fixture inBack = CreateSingleMove("InBack", duration: 4);
        double dip = EvaluateAt(inBack, 0.3).Rotation;
        Require(dip < -5, $"InBack undershoot was clamped: {dip}.");

        Fixture outElastic = CreateSingleMove("OutElastic", duration: 4);
        double ring = EvaluateAt(outElastic, 0.1).Rotation;
        Require(ring > 100, $"OutElastic overshoot was clamped: {ring}.");
    }

    private static void VerifyZeroDurationAppliesTarget()
    {
        foreach (string ease in new[] { "OutCirc", "OutElastic", "NotAnEase" })
        {
            Fixture fixture = CreateSingleMove(ease, duration: 0);
            RequireNear(100,
                DecorationState.Evaluate(fixture.Decoration, fixture.Timeline, fixture.Occurrence.StartTime).Rotation,
                $"duration 0 {ease} did not apply its target at the event start");
        }
    }

    private static void VerifyEaseNameParsing()
    {
        // Enum.Parse semantics: case-sensitive names, numeric values.
        RequireProgress("21", 0.5, Oracle("OutCirc", 0.5), "numeric string 21 (OutCirc)");
        RequireProgress(" OutCirc ", 0.5, Oracle("OutCirc", 0.5), "surrounding whitespace");
        RequireProgress("Unset", 0.5, Oracle("OutQuad", 0.5), "Unset uses the DOTween default branch");
        RequireProgress("99", 0.5, Oracle("OutQuad", 0.5), "an undefined numeric value uses the default branch");
        RequireProgress("INTERNAL_Zero", 0.5, 1, "INTERNAL_Zero");

        // Names the game would reject keep the event and fall back to Linear.
        foreach (string unknown in new[] { "outcirc", "NotAnEase", "INTERNAL_Custom" })
        {
            Require(!DotweenEaseEvaluator.IsSupported(unknown),
                $"{unknown} was reported as a supported ease.");
            Fixture fixture = CreateSingleMove(unknown, duration: 4);
            RequireNear(50, EvaluateAt(fixture, 0.5).Rotation, $"unknown ease {unknown} at mid-point");
            RequireNear(100, EvaluateAt(fixture, 1.5).Rotation, $"unknown ease {unknown} after the end");
        }

        // A JSON number is parsed like Enum.ToObject.
        Fixture numeric = CreateSingleMove(null, duration: 4, easeNode: JsonValue.Create(21));
        RequireNear(100 * Oracle("OutCirc", 0.5), EvaluateAt(numeric, 0.5).Rotation, "JSON numeric ease 21");
    }

    private static void VerifySuccessiveEvents()
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("chain");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "chain", angleOffset: 0, duration: 4, ease: "OutCirc", rotation: 100),
            CreateMove(0, 1, "chain", angleOffset: 360, duration: 2, ease: "OutElastic", rotation: 300),
            CreateMove(0, 2, "chain", angleOffset: 1800, duration: 2, ease: "InCirc", rotation: -100)
        ]);
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence first = Find(timeline, 0, false);
        VfxOccurrence second = Find(timeline, 1, false);
        VfxOccurrence third = Find(timeline, 2, false);
        double firstProgress = (second.StartTime - first.StartTime) / first.DurationSeconds!.Value;
        Require(firstProgress is > 0.2 and < 0.8 &&
                third.StartTime > second.StartTime + second.DurationSeconds!.Value,
            "Successive-event fixture timing is not as intended.");

        // The second event starts part-way through the first; DOTween kills the
        // first with complete, so the second tweens from 100.
        RequireNear(100 * Oracle("OutCirc", firstProgress - 1e-9 / first.DurationSeconds!.Value),
            DecorationState.Evaluate(decoration, timeline, second.StartTime - 1e-9).Rotation,
            "first OutCirc event just before the second starts");
        RequireNear(100 + 200 * Oracle("OutElastic", 0.5),
            DecorationState.Evaluate(decoration, timeline, second.StartTime + second.DurationSeconds!.Value * 0.5).Rotation,
            "second OutElastic event from the completed first");
        RequireNear(300 - 400 * Oracle("InCirc", 0.25),
            DecorationState.Evaluate(decoration, timeline, third.StartTime + third.DurationSeconds!.Value * 0.25).Rotation,
            "third InCirc event after the second");
    }

    private static void VerifyRepeatEventsKeepEase()
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("repeat");
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "repeat", angleOffset: 0, duration: 1, ease: "OutCirc", rotation: 100, eventTag: "rep"),
            CreateRepeat(0, 1, "rep", interval: 2),
            CreateMove(0, 2, "repeat", angleOffset: 270, duration: 0, ease: "Linear", rotation: 0)
        ]);
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence repeated = Find(timeline, 0, true);
        RequireNear(100 * Oracle("OutCirc", 0.5),
            DecorationState.Evaluate(decoration, timeline,
                repeated.StartTime + repeated.DurationSeconds!.Value * 0.5).Rotation,
            "RepeatEvents copy of an OutCirc MoveDecorations");
    }

    private static void VerifySeekingBackwardIsStateless()
    {
        Fixture fixture = CreateSingleMove("InOutCirc", duration: 4);
        double late = EvaluateAt(fixture, 0.9).Rotation;
        double early = EvaluateAt(fixture, 0.2).Rotation;
        double lateAgain = EvaluateAt(fixture, 0.9).Rotation;
        RequireNear(100 * Oracle("InOutCirc", 0.2), early, "InOutCirc after seeking back");
        RequireNear(late, lateAgain, "InOutCirc after seeking forward again");
    }

    private static void VerifyDegenerateInputsStayFinite()
    {
        foreach (string ease in DotweenEases)
        {
            foreach ((double elapsed, double duration) in new[]
                     {
                         (1e-30, 2e-30), (0.5, 1e-38), (1e6, 2e6), (-1.0, 4.0), (8.0, 4.0)
                     })
            {
                if (DotweenEaseEvaluator.TryEvaluate(ease, elapsed, duration, out float value))
                    Require(float.IsFinite(value), $"{ease} produced {value} for {elapsed}/{duration}.");
            }
            Require(!DotweenEaseEvaluator.TryEvaluate(ease, double.NaN, 4, out _) &&
                    !DotweenEaseEvaluator.TryEvaluate(ease, 1, double.PositiveInfinity, out _),
                $"{ease} accepted a non-finite time.");
        }
    }

    // ---- DOTween reference curves on normalized time, transcribed from
    // DG.Tweening.Core.Easing.EaseManager / Bounce / Flash (double precision).
    internal static double Oracle(string ease, double t)
    {
        const double s = Overshoot;
        switch (ease)
        {
            case "Linear": return t;
            case "InSine": return 1 - Math.Cos(t * Math.PI / 2);
            case "OutSine": return Math.Sin(t * Math.PI / 2);
            case "InOutSine": return -0.5 * (Math.Cos(Math.PI * t) - 1);
            case "InQuad": return t * t;
            case "OutQuad": return -t * (t - 2);
            case "InOutQuad": return InOutPower(t, 2);
            case "InCubic": return Math.Pow(t, 3);
            case "OutCubic": return Math.Pow(t - 1, 3) + 1;
            case "InOutCubic": return InOutPower(t, 3);
            case "InQuart": return Math.Pow(t, 4);
            case "OutQuart": return -(Math.Pow(t - 1, 4) - 1);
            case "InOutQuart": return InOutPower(t, 4);
            case "InQuint": return Math.Pow(t, 5);
            case "OutQuint": return Math.Pow(t - 1, 5) + 1;
            case "InOutQuint": return InOutPower(t, 5);
            case "InExpo": return t == 0 ? 0 : Math.Pow(2, 10 * (t - 1));
            case "OutExpo": return t == 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            case "InOutExpo":
            {
                if (t == 0) return 0;
                if (t == 1) return 1;
                double u = t * 2;
                return u < 1 ? 0.5 * Math.Pow(2, 10 * (u - 1)) : 0.5 * (2 - Math.Pow(2, -10 * (u - 1)));
            }
            case "InCirc": return 1 - Math.Sqrt(1 - t * t);
            case "OutCirc": return Math.Sqrt(1 - (t - 1) * (t - 1));
            case "InOutCirc":
            {
                double u = t * 2;
                return u < 1 ? -0.5 * (Math.Sqrt(1 - u * u) - 1) : 0.5 * (Math.Sqrt(1 - (u - 2) * (u - 2)) + 1);
            }
            case "InElastic":
            {
                if (t == 0) return 0;
                if (t == 1) return 1;
                const double p = 0.3;
                double shift = p / (2 * Math.PI) * Math.Asin(1 / s);
                return -(s * Math.Pow(2, 10 * (t - 1)) * Math.Sin((t - 1 - shift) * 2 * Math.PI / p));
            }
            case "OutElastic":
            {
                if (t == 0) return 0;
                if (t == 1) return 1;
                const double p = 0.3;
                double shift = p / (2 * Math.PI) * Math.Asin(1 / s);
                return s * Math.Pow(2, -10 * t) * Math.Sin((t - shift) * 2 * Math.PI / p) + 1;
            }
            case "InOutElastic":
            {
                if (t == 0) return 0;
                double u = t * 2;
                if (u == 2) return 1;
                const double p = 0.45;
                double shift = p / (2 * Math.PI) * Math.Asin(1 / s);
                double v = u - 1;
                double wave = Math.Sin((v - shift) * 2 * Math.PI / p);
                return u < 1
                    ? -0.5 * (s * Math.Pow(2, 10 * v) * wave)
                    : s * Math.Pow(2, -10 * v) * wave * 0.5 + 1;
            }
            case "InBack": return t * t * ((s + 1) * t - s);
            case "OutBack":
            {
                double u = t - 1;
                return u * u * ((s + 1) * u + s) + 1;
            }
            case "InOutBack":
            {
                double s2 = s * 1.525;
                double u = t * 2;
                if (u < 1)
                    return 0.5 * (u * u * ((s2 + 1) * u - s2));
                u -= 2;
                return 0.5 * (u * u * ((s2 + 1) * u + s2) + 2);
            }
            case "InBounce": return 1 - OutBounce(1 - t);
            case "OutBounce": return OutBounce(t);
            case "InOutBounce": return t < 0.5 ? (1 - OutBounce(1 - t * 2)) * 0.5 : OutBounce(t * 2 - 1) * 0.5 + 0.5;
            case "Flash":
            case "InFlash":
            case "OutFlash":
            case "InOutFlash":
                return FlashOracle(ease, t);
            default:
                throw new InvalidOperationException($"No DOTween oracle for {ease}.");
        }
    }

    private static double InOutPower(double t, int power)
    {
        double u = t * 2;
        if (u < 1)
            return 0.5 * Math.Pow(u, power);
        u -= 2;
        return power % 2 == 0 ? -0.5 * (Math.Pow(u, power) - 2) : 0.5 * (Math.Pow(u, power) + 2);
    }

    private static double OutBounce(double t)
    {
        if (t < 1 / 2.75) return 7.5625 * t * t;
        if (t < 2 / 2.75) { t -= 1.5 / 2.75; return 7.5625 * t * t + 0.75; }
        if (t < 2.5 / 2.75) { t -= 2.25 / 2.75; return 7.5625 * t * t + 0.9375; }
        t -= 2.625 / 2.75;
        return 7.5625 * t * t + 0.984375;
    }

    private static double FlashOracle(string ease, double t)
    {
        // Period 0: WeightedEase only caps the result at 1.
        const double flashes = Overshoot;
        int step = (int)Math.Ceiling(t * flashes);
        double stepDuration = 1 / flashes;
        double time = t - stepDuration * (step - 1);
        double dir = step % 2 != 0 ? 1 : -1;
        if (dir < 0)
            time -= stepDuration;
        double result;
        switch (ease)
        {
            case "Flash":
                result = time * dir / stepDuration;
                break;
            case "InFlash":
                time = time * dir / stepDuration;
                result = time * time;
                break;
            case "OutFlash":
                time = time * dir / stepDuration;
                result = -time * (time - 2);
                break;
            default:
                time = time * dir / (stepDuration * 0.5);
                if (time < 1)
                {
                    result = 0.5 * time * time;
                }
                else
                {
                    time -= 1;
                    result = -0.5 * (time * (time - 2) - 1);
                }
                break;
        }
        return Math.Min(result, 1);
    }

    // ---- fixtures

    private static void RequireProgress(string ease, double progress, double expected, string label)
    {
        Fixture fixture = CreateSingleMove(ease, duration: 4);
        RequireNear(100 * expected, EvaluateAt(fixture, progress).Rotation, label);
    }

    private static Fixture CreateSingleMove(
        string? ease,
        double duration,
        bool allProperties = false,
        JsonNode? easeNode = null)
    {
        LevelDocument level = CreateLevel();
        LevelDecoration decoration = CreateDecoration("target");
        level.ReplaceDecorations([decoration]);
        LevelAction move = CreateMove(0, 0, "target", angleOffset: 180, duration, ease,
            rotation: 100,
            position: allProperties ? (100, 100) : null,
            scale: allProperties ? (100, 100) : null,
            opacity: allProperties ? 100 : null);
        if (easeNode is not null)
            move.PropertyOverrides!["ease"] = easeNode;
        level.ReplaceActions([move]);
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        return new Fixture(decoration, timeline, Find(timeline, 0, false));
    }

    private static DecorationState EvaluateAt(Fixture fixture, double progress) =>
        DecorationState.Evaluate(
            fixture.Decoration,
            fixture.Timeline,
            fixture.Occurrence.StartTime + fixture.Occurrence.DurationSeconds!.Value * progress);

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
                ["opacity"] = 0
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
        if (position is { } p)
            properties["positionOffset"] = new JsonArray(p.X, p.Y);
        if (rotation is double r)
            properties["rotationOffset"] = r;
        if (scale is { } sc)
            properties["scale"] = new JsonArray(sc.X, sc.Y);
        if (opacity is double o)
            properties["opacity"] = o;
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

    private static LevelAction CreateRepeat(int floor, int sourceIndex, string tag, double interval) =>
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

    private static VfxOccurrence Find(VfxTimeline timeline, int sourceIndex, bool repeated) =>
        timeline.Occurrences.Single(o => o.SourceIndex == sourceIndex && o.IsRepeated == repeated);

    private static void RequireNear(double expected, double actual, string label)
    {
        if (!(Math.Abs(expected - actual) <= StateTolerance))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Fixture(LevelDecoration Decoration, VfxTimeline Timeline, VfxOccurrence Occurrence);
}
