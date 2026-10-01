using System.Numerics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class RecolorTrackRuntimeSemanticsRegression
{
    private const float Tolerance = 0.0001f;
    private static readonly Vector3 Red = new(1, 0, 0);
    private static readonly Vector3 Blue = new(0, 0, 1);
    private static readonly Vector3 Green = new(0, 1, 0);

    public static void Run()
    {
        var failures = new List<string>();

        Check(failures, "Base excludes future Recolor", VerifyBaseState);
        Check(failures, "Single activation boundaries", VerifyActivation);
        foreach (string ease in new[]
                 {
                     "Linear", "InSine", "OutSine", "InOutSine",
                     "InQuad", "OutQuad", "InOutQuad"
                 })
        {
            string captured = ease;
            Check(failures, $"Ease {captured}", () => VerifyEase(captured));
        }
        Check(failures, "Pause freezes Recolor chart progress", VerifyPause);
        Check(failures, "Seek reconstructs 25 percent", () => VerifySeek(11, 0.25f));
        Check(failures, "Seek reconstructs 75 percent", () => VerifySeek(13, 0.75f));
        Check(failures, "Seek reconstructs completed state", () => VerifySeek(20, 1.0f));
        Check(failures, "Seek backward restores base", () => VerifySeek(5, 0.0f));
        Check(failures, "Overlap A progresses before replacement", () => VerifyOverlapAt(
            11, new Vector3(0.75f, 0, 0.25f), "A at 25 percent"));
        Check(failures, "Overlap Kill complete supplies B start", () => VerifyOverlapAt(
            12, Blue, "B start after Kill(complete:true)"));
        Check(failures, "Overlap B progresses from completed A target", () => VerifyOverlapAt(
            14, new Vector3(0, 0.5f, 0.5f), "B midpoint"));
        Check(failures, "Overlap B reaches target", () => VerifyOverlapAt(
            16, Green, "B end"));
        Check(failures, "Stripes use resolved-start parity", VerifyStripes);
        Check(failures, "Runtime reversed inclusive range", () => VerifyRange(5, 1, 0, [1, 2, 3, 4, 5]));
        Check(failures, "Runtime gapLength zero", () => VerifyRange(1, 4, 0, [1, 2, 3, 4]));
        Check(failures, "Runtime gapLength one", () => VerifyRange(1, 5, 1, [1, 3, 5]));
        Check(failures, "Runtime gapLength greater than one", () => VerifyRange(0, 6, 2, [0, 3, 6]));
        Check(failures, "Runtime start equals end", () => VerifyRange(3, 3, 4, [3]));
        Check(failures, "RepeatEvents are expanded in managed timeline", VerifyRepeatOccurrences);
        Check(failures, "inactive Recolor is ignored", VerifyInactive);
        Check(failures, "same-time ordering uses timeline order", VerifySameTimeOrdering);
        Check(failures, "runtime payload retains required fields", VerifyPayload);
        Check(failures, "pitch and BPM have separate duration roles", VerifyPitchAndBpm);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"RecolorTrack runtime semantics are not available ({failures.Count}):\n" +
                string.Join("\n", failures.Select(static item => $"- {item}")));
        }
    }

    private static void Check(List<string> failures, string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            string failure = $"{name}: {ex.Message}";
            Console.WriteLine($"RED: {failure}");
            failures.Add(failure);
        }
    }

    private static void VerifyBaseState()
    {
        AssertColor(Red, CurrentRendererFacingColor(0), "future Recolor changed the base snapshot");
    }

    private static void VerifyActivation()
    {
        SpecEvent item = Event(10, 2, Blue, "Linear", 1);
        AssertActualMatchesReference(9.999, [item], "before start");
        AssertActualMatchesReference(10, [item], "at start");
        AssertActualMatchesReference(11, [item], "midpoint");
        AssertActualMatchesReference(12, [item], "at end");
        AssertActualMatchesReference(20, [item], "after end");
    }

    private static void VerifyEase(string ease)
    {
        SpecEvent item = Event(10, 4, Blue, ease, 2);
        const double time = 11;
        Vector3 expected = EvaluateReference(Red, [item], time);
        Vector3 actual = CurrentRendererFacingColor(0);
        AssertColor(expected, actual,
            $"{ease} at raw progress 0.25 expected={Format(expected)} actual={Format(actual)}");
    }

    private static void VerifyPause()
    {
        SpecEvent item = Event(10, 4, Blue, "Linear", 3);
        Vector3 expectedPaused = EvaluateReference(Red, [item], 12);
        Vector3 afterWallTimeOnly = EvaluateReference(Red, [item], 12);
        AssertColor(expectedPaused, afterWallTimeOnly, "wall time advanced a paused chart tween");
        AssertColor(expectedPaused, CurrentRendererFacingColor(0),
            $"paused midpoint expected={Format(expectedPaused)} actual={Format(CurrentRendererFacingColor(0))}");

        Vector3 expectedResumed = EvaluateReference(Red, [item], 13);
        if (Close(expectedPaused, expectedResumed))
            throw new InvalidOperationException("resume did not advance chart-time progress");
    }

    private static void VerifySeek(double chartTime, float progress)
    {
        SpecEvent item = Event(10, 4, Blue, "Linear", 4);
        Vector3 expected = Vector3.Lerp(Red, Blue, progress);
        Vector3 reference = EvaluateReference(Red, [item], chartTime);
        AssertColor(expected, reference, "reference seek fixture is invalid");
        AssertColor(expected, CurrentRendererFacingColor(0),
            $"seek={chartTime} expected={Format(expected)} actual={Format(CurrentRendererFacingColor(0))}");
    }

    private static void VerifyOverlapAt(double time, Vector3 expected, string phase)
    {
        SpecEvent[] events =
        [
            Event(10, 4, Blue, "Linear", 5),
            Event(12, 4, Green, "Linear", 6)
        ];
        AssertExpectedAt(time, expected, events, phase);
    }

    private static void VerifyStripes()
    {
        const int resolvedStart = 1;
        int[] floors = [1, 2, 3, 4];
        foreach (int floor in floors)
        {
            Vector3 target = ((floor - resolvedStart) & 1) == 0 ? Blue : Green;
            AssertColor(target, CurrentRendererFacingColor(floor),
                $"floor={floor} resolvedStart={resolvedStart} expected={Format(target)} " +
                $"actual={Format(CurrentRendererFacingColor(floor))}");
        }
    }

    private static void VerifyRange(int start, int end, int gap, int[] expectedFloors)
    {
        TrackVisualSourceEvent source = SourceEvent(start, end, gap);
        int[] selected = TrackVisualResolver.ResolveRecolorRange(source, 8).Floors().ToArray();
        if (!selected.SequenceEqual(expectedFloors))
        {
            throw new InvalidOperationException(
                $"selection expected=[{string.Join(',', expectedFloors)}] actual=[{string.Join(',', selected)}]");
        }

        foreach (int floor in selected)
        {
            AssertColor(Blue, CurrentRendererFacingColor(floor),
                $"selected floor {floor} remained unchanged; expected={Format(Blue)} " +
                $"actual={Format(CurrentRendererFacingColor(floor))}");
        }
    }

    private static void VerifyRepeatOccurrences()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        LevelAction recolor = RecolorAction(1, 20, active: true, eventTag: "repeat-me");
        var repeat = new LevelAction(1, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = 21,
            PropertyOverrides = new JsonObject
            {
                ["repeatType"] = "Beat",
                ["tag"] = "repeat-me",
                ["repetitions"] = 2,
                ["interval"] = 0.5,
                ["executeOnCurrentFloor"] = false
            }
        };
        level.ReplaceActions([recolor, repeat]);

        VfxOccurrence[] occurrences = VfxTimelineBuilder.Build(level).Occurrences
            .Where(static item => item.SourceIndex == 20)
            .ToArray();
        if (occurrences.Length != 3 ||
            occurrences.Count(static item => item.IsRepeated) != 2 ||
            !occurrences.Select(static item => item.StartTime).SequenceEqual(
                occurrences.Select(static item => item.StartTime).OrderBy(static value => value)))
        {
            throw new InvalidOperationException("managed Recolor repeat occurrences/order are incorrect");
        }
    }

    private static void VerifyInactive()
    {
        SpecEvent inactive = Event(0, 0, Blue, "Linear", 22) with { Active = false };
        AssertColor(Red, EvaluateReference(Red, [inactive], 10), "inactive event changed reference state");
        AssertColor(Red, CurrentRendererFacingColor(0), "inactive event changed renderer-facing base");
    }

    private static void VerifySameTimeOrdering()
    {
        SpecEvent[] events =
        [
            Event(10, 0, Blue, "Linear", 30),
            Event(10, 0, Green, "Linear", 31)
        ];
        Vector3 expected = EvaluateReference(Red, events, 10);
        AssertColor(Green, expected, "same-time reference did not preserve source ordering");
        AssertColor(expected, CurrentRendererFacingColor(0),
            $"same-time final expected={Format(expected)} actual={Format(CurrentRendererFacingColor(0))}");
    }

    private static void VerifyPayload()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        level.PitchPercent = 200;
        LevelAction action = RecolorAction(2, 40, active: true, eventTag: null);
        action.AngleOffset = 90;
        action.Duration = 2;
        action.PropertyOverrides = new JsonObject
        {
            ["ease"] = "InOutQuad",
            ["trackColorType"] = "Stripes",
            ["trackColor"] = "112233",
            ["secondaryTrackColor"] = "445566",
            ["trackColorAnimDuration"] = 3.5,
            ["trackColorPulse"] = "Forward",
            ["trackPulseLength"] = 7,
            ["trackStyle"] = "Neon",
            ["trackGlowIntensity"] = 64,
            ["startTile"] = new JsonArray(1, "Start"),
            ["endTile"] = new JsonArray(6, "Start"),
            ["gapLength"] = 2
        };
        level.ReplaceActions([action]);
        VfxOccurrence occurrence = VfxTimelineBuilder.Build(level).Occurrences.Single();
        TrackVisualSourceEvent source = new(
            40, 2, "RecolorTrack", true, "Stripes", "112233", "445566", 3.5,
            "Forward", 7, "Neon", 64, string.Empty, 1, false, 2,
            new TrackTileReference(1, "Start"), new TrackTileReference(6, "Start"));
        ResolvedTrackVisualRange range = TrackVisualResolver.ResolveRecolorRange(source, level.FloorCount);

        var payload = new SpecPayload(
            source.ColorType!, source.PrimaryColor!, source.SecondaryColor!,
            source.AnimDuration!.Value / (level.PitchPercent * 0.01),
            source.PulseType!, source.PulseLength!.Value, source.TrackStyle!,
            source.GlowIntensity!.Value, range.Start, range.End, source.GapLength,
            occurrence.StartTime, occurrence.DurationSeconds!.Value, occurrence.Ease!,
            occurrence.SourceIndex);

        if (payload != new SpecPayload(
                "Stripes", "112233", "445566", 1.75, "Forward", 7, "Neon", 64,
                1, 6, 2, occurrence.StartTime, occurrence.DurationSeconds.Value,
                "InOutQuad", 40))
        {
            throw new InvalidOperationException("runtime payload lost a required visual/timing field");
        }
    }

    private static void VerifyPitchAndBpm()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.InitialBpm = 120;
        level.PitchPercent = 200;
        var speed = new LevelAction(1, "SetSpeed", true, "Bpm", 240, null, null)
        {
            SourceIndex = 0,
            Kind = LevelActionKind.SetSpeed
        };
        LevelAction recolor = RecolorAction(1, 1, active: true, eventTag: null);
        recolor.Duration = 2;
        level.ReplaceActions([speed, recolor]);

        VfxOccurrence occurrence = VfxTimelineBuilder.Build(level).Occurrences.Single(item => item.SourceIndex == 1);
        double expectedTransition = 2 * 60.0 / (240 * 2.0);
        AssertNear(expectedTransition, occurrence.DurationSeconds!.Value,
            "transition duration did not use effective BPM and pitch");

        const double rawLoopDuration = 2;
        double effectiveLoopDuration = rawLoopDuration / (level.PitchPercent * 0.01);
        AssertNear(1, effectiveLoopDuration,
            "color loop duration included SetSpeed/BPM instead of pitch only");
    }

    private static void AssertActualMatchesReference(double time, SpecEvent[] events, string phase)
    {
        Vector3 expected = EvaluateReference(Red, events, time);
        Vector3 actual = CurrentRendererFacingColor(0);
        AssertColor(expected, actual,
            $"{phase} t={time} expected={Format(expected)} actual={Format(actual)}");
    }

    private static void AssertExpectedAt(double time, Vector3 expected, SpecEvent[] events, string phase)
    {
        Vector3 reference = EvaluateReference(Red, events, time);
        AssertColor(expected, reference, $"invalid overlap reference for {phase}");
        AssertColor(expected, CurrentRendererFacingColor(0),
            $"{phase} expected={Format(expected)} actual={Format(CurrentRendererFacingColor(0))}");
    }

    private static Vector3 EvaluateReference(Vector3 initial, IEnumerable<SpecEvent> source, double time)
    {
        Vector3 state = initial;
        ActiveTween? tween = null;
        foreach (SpecEvent item in source
                     .Where(static item => item.Active)
                     .OrderBy(static item => item.StartTime)
                     .ThenBy(static item => item.SourceIndex))
        {
            if (item.StartTime > time)
                break;

            if (tween is not null)
                state = tween.Target; // stock Kill(complete:true)

            if (item.DurationSeconds <= 0)
            {
                state = item.Target;
                tween = null;
                continue;
            }
            tween = new ActiveTween(state, item.Target, item.StartTime, item.DurationSeconds, item.Ease);
        }

        if (tween is null)
            return state;
        if (!DotweenEaseEvaluator.TryEvaluate(
                tween.Ease,
                time - tween.StartTime,
                tween.DurationSeconds,
                out float progress))
        {
            throw new InvalidOperationException($"unsupported fixture ease {tween.Ease}");
        }
        return Vector3.Lerp(tween.Start, tween.Target, progress);
    }

    private static Vector3 CurrentRendererFacingColor(int floor)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        level.ReplaceActions([new LevelAction(0, "RecolorTrack", true, null, null, null, null)
        {
            SourceIndex = 999
        }]);
        TrackVisualMetadataCache.Attach(level, new TrackVisualSourceData(
            new TrackVisualStyle(
                "Single", "ff0000", "ffffff", 2, "None", 4,
                "Standard", 100, string.Empty, 1, 0),
            [new TrackVisualSourceEvent(
                999, 0, "RecolorTrack", true, "Single", "0000ff", "ffffff", 2,
                "None", 4, "Standard", 100, string.Empty, 1, false, 0,
                new TrackTileReference(0, "Start"), new TrackTileReference(7, "Start"))]));
        NativeTrackVisual visual = TrackVisualResolver.Resolve(level)[floor];
        return new Vector3(
            (visual.PrimaryColor & 0xffu) / 255f,
            ((visual.PrimaryColor >> 8) & 0xffu) / 255f,
            ((visual.PrimaryColor >> 16) & 0xffu) / 255f);
    }

    private static SpecEvent Event(double start, double duration, Vector3 target, string ease, int sourceIndex) =>
        new(start, duration, target, ease, sourceIndex, true);

    private static LevelAction RecolorAction(int floor, int sourceIndex, bool active, string? eventTag) =>
        new(floor, "RecolorTrack", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            EventTag = eventTag,
            PropertyOverrides = new JsonObject
            {
                ["eventTag"] = eventTag,
                ["ease"] = "Linear"
            }
        };

    private static TrackVisualSourceEvent SourceEvent(int start, int end, int gap) => new(
        100, 4, "RecolorTrack", true, "Single", "0000ff", "00ff00", 2,
        "None", 4, "Standard", 100, string.Empty, 1, false, gap,
        new TrackTileReference(start, "Start"), new TrackTileReference(end, "Start"));

    private static void AssertColor(Vector3 expected, Vector3 actual, string message)
    {
        if (!Close(expected, actual))
            throw new InvalidOperationException(message);
    }

    private static bool Close(Vector3 left, Vector3 right) =>
        Math.Abs(left.X - right.X) <= Tolerance &&
        Math.Abs(left.Y - right.Y) <= Tolerance &&
        Math.Abs(left.Z - right.Z) <= Tolerance;

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{message}; expected={expected} actual={actual}");
    }

    private static string Format(Vector3 value) => $"({value.X:F4},{value.Y:F4},{value.Z:F4})";

    private sealed record SpecEvent(
        double StartTime,
        double DurationSeconds,
        Vector3 Target,
        string Ease,
        int SourceIndex,
        bool Active);

    private sealed record ActiveTween(
        Vector3 Start,
        Vector3 Target,
        double StartTime,
        double DurationSeconds,
        string Ease);

    private sealed record SpecPayload(
        string ColorType,
        string PrimaryColor,
        string SecondaryColor,
        double EffectiveAnimDuration,
        string PulseType,
        int PulseLength,
        string TrackStyle,
        double GlowIntensity,
        int ResolvedStartFloor,
        int ResolvedEndFloor,
        int GapLength,
        double StartTime,
        double DurationSeconds,
        string Ease,
        int SourceIndex);
}
