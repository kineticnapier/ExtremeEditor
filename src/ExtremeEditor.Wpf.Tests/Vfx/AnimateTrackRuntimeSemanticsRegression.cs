using System.Numerics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class AnimateTrackRuntimeSemanticsRegression
{
    private const double Tolerance = 0.000001;

    public static void Run()
    {
        var failures = new List<string>();

        RunPass("defaults and persistent state", VerifyPersistentState);
        RunPass("normal appear timing", VerifyNormalAppearTiming);
        RunPass("Drop/Rise timing and pitch", VerifyVerticalAppearTiming);
        RunPass("disappear timing and terminal floor", VerifyDisappearTiming);
        RunPass("SetSpeed boundary correction", VerifySpeedBoundaryCorrection);
        RunPass("appear transform contracts", VerifyAppearTransforms);
        RunPass("disappear transform contracts", VerifyDisappearTransforms);
        RunPass("pause and deterministic seek", VerifyPauseAndSeek);
        RunPass("MoveTrack tween-key competition", VerifyTweenKeyCompetition);
        RunPass("PositionTrack interaction", VerifyPositionTrackInteraction);
        RunPass("special runtime capability contract", VerifySpecialCapabilities);
        RunPass("compact persistent segment contract", VerifyCompactSegments);

        CheckRed(failures, "Persistent AnimateTrack reaches renderer timeline",
            VerifyProductionPersistentSegments);
        CheckRed(failures, "Normal appear timing reaches renderer timeline",
            VerifyProductionNormalTiming);
        CheckRed(failures, "Drop/Rise special timing reaches renderer timeline",
            VerifyProductionVerticalTiming);
        CheckRed(failures, "Disappear timing reaches renderer timeline",
            VerifyProductionDisappearTiming);
        CheckRed(failures, "Appear transforms reach renderer timeline",
            VerifyProductionAppearTypes);
        CheckRed(failures, "Disappear transforms reach renderer timeline",
            VerifyProductionDisappearTypes);
        CheckRed(failures, "AnimateTrack participates in chart-time reconstruction",
            VerifyProductionChartReconstruction);
        CheckRed(failures, "Animate/MoveTrack competition is representable",
            VerifyProductionCompetitionRepresentation);
        CheckRed(failures, "PositionTrack base feeds AnimateTrack targets",
            VerifyProductionPositionTrackInteraction);
        CheckRed(failures, "Extend/Retract special state is representable",
            VerifyProductionSpecialCapabilities);
        CheckRed(failures, "AnimateTrack uses compact persistent runtime segments",
            VerifyProductionCompactSegments);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"AnimateTrack runtime semantics are not available ({failures.Count}):\n" +
                string.Join("\n", failures.Select(static item => $"- {item}")));
        }
    }

    private static void RunPass(string name, Action test)
    {
        test();
        Console.WriteLine($"PASS: AnimateTrack spec fixture: {name}");
    }

    private static void CheckRed(List<string> failures, string name, Action test)
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

    private static void VerifyPersistentState()
    {
        AnimateChange[] changes =
        [
            new(2, true, "Grow", 2, "Fade", 5),
            new(4, false, "Drop", 9, "Retract", 9),
            new(5, true, "Rise", 1, "Shrink", 2)
        ];
        AnimateState[] states = ResolveStates(8, changes);
        AssertState(DefaultState, states[0], "default floor");
        AssertState(new("Grow", 2, "Fade", 5), states[2], "event floor");
        AssertState(new("Grow", 2, "Fade", 5), states[4], "inactive event");
        AssertState(new("Rise", 1, "Shrink", 2), states[5], "replacement floor");
        AssertState(new("Rise", 1, "Shrink", 2), states[7], "replacement persistence");
    }

    private static void VerifyNormalAppearTiming()
    {
        Timing timing = AppearTiming("Grow", entryTime: 10, bpm: 120, speed: 2, pitch: 1, beats: 3);
        AssertNear(9.25, timing.Start, "normal start");
        AssertNear(0.125, timing.Duration, "normal duration");
        AssertNear(0, AppearTiming("Fade", 0.2, 120, 1, 1, 3).Start, "first-floor clamp");
    }

    private static void VerifyVerticalAppearTiming()
    {
        Timing drop100 = AppearTiming("Drop", 10, 120, 2, 1, 3);
        Timing drop200 = AppearTiming("Drop", 10, 120, 2, 2, 3);
        Timing rise200 = AppearTiming("Rise", 10, 120, 2, 2, 3);
        AssertNear(8.5, drop100.Start, "Drop doubled lead");
        AssertNear(0.75, drop100.Duration, "Drop duration");
        AssertNear(drop100.Start, drop200.Start, "pitch changed Drop start");
        AssertNear(0.375, drop200.Duration, "pitch did not shorten Drop duration");
        AssertNear(drop200.Start, rise200.Start, "Rise start differs from Drop");
        AssertNear(drop200.Duration, rise200.Duration, "Rise duration differs from Drop");
    }

    private static void VerifyDisappearTiming()
    {
        Timing timing = DisappearTiming(entryTime: 10, nextEntryTime: 10.75, bpm: 120,
            speed: 2, pitch: 2, beats: 4, hasNext: true)!.Value;
        AssertNear(11.75, timing.Start, "disappear start");
        AssertNear(0.0625, timing.Duration, "disappear duration");
        if (DisappearTiming(10, 0, 120, 2, 1, 4, hasNext: false) is not null)
            throw new InvalidOperationException("last floor produced a disappear effect");
    }

    private static void VerifySpeedBoundaryCorrection()
    {
        AssertNear(6, EffectiveBeats(3, floorSpeed: 2, referenceSpeed: 1), "appear correction");
        AssertNear(2, EffectiveBeats(4, floorSpeed: 1, referenceSpeed: 2), "disappear correction");
        // Stock's branch is selected by whether the appear property is enabled, for both values.
        double enabled = SelectCorrectedBeats(4, 2, currentReference: 2, previousReference: 1, appearEnabled: true);
        double disabled = SelectCorrectedBeats(4, 2, currentReference: 2, previousReference: 1, appearEnabled: false);
        AssertNear(4, enabled, "enabled branch");
        AssertNear(8, disabled, "disabled branch");
    }

    private static void VerifyAppearTransforms()
    {
        Vector2 start = new(10, 20);
        Vector2 previous = new(3, 4);
        Vector2 nearRandom = new(3.5f, -2.5f);
        Vector2 farRandom = new(7.5f, -7.5f);

        Effect none = AppearEffect("None", start, previous, 30, nearRandom, 45, 4);
        if (none.Tweens.Count != 0) throw new InvalidOperationException("None created a tween");

        Effect assemble = AppearEffect("Assemble", start, previous, 30, nearRandom, 45, 4);
        AssertVector(start + nearRandom, assemble.InitialPosition, "Assemble initial position");
        AssertNear(75, assemble.InitialRotation, "Assemble initial rotation");
        RequireTween(assemble, "Position", start, 4, "OutSine");
        RequireTween(assemble, "Rotation", 30, 4, "OutSine");

        Effect far = AppearEffect("Assemble_Far", start, previous, 30, farRandom, -70, 4);
        AssertVector(start + farRandom, far.InitialPosition, "Assemble_Far initial position");
        AssertThrows(() => AppearEffect("Assemble", start, previous, 30, new Vector2(4.01f, 0), 0, 4),
            "Assemble accepted a random position outside -4..4");
        AssertThrows(() => AppearEffect("Assemble_Far", start, previous, 30, default, 75.01, 4),
            "Assemble_Far accepted a rotation outside -75..75");

        Effect extend = AppearEffect("Extend", start, previous, 30, default, 0, 4);
        AssertVector(previous, extend.InitialPosition, "Extend previous-floor position");
        AssertNear(0, extend.InitialScale, "Extend initial scale");
        AssertNear(0, extend.ExtendAnim, "Extend initial mesh fraction");
        RequireTween(extend, "Position", start, 4, "OutSine");
        RequireTween(extend, "Scale", 1, 4, "OutSine");
        RequireTween(extend, "ExtendAnim", 1, 4, "OutSine");

        Effect grow = AppearEffect("Grow", start, previous, 30, default, 0, 4);
        RequireTween(grow, "Scale", 1, 4, "OutQuad");
        Effect spin = AppearEffect("Grow_Spin", start, previous, 30, default, 0, 4);
        AssertNear(-150, spin.InitialRotation, "Grow_Spin initial rotation");
        RequireTween(spin, "Rotation", 30, 4, "OutSine");
        Effect fade = AppearEffect("Fade", start, previous, 30, default, 0, 4);
        AssertNear(0, fade.InitialOpacity, "Fade initial opacity");
        RequireTween(fade, "Opacity", 1, 4, "Linear");

        Effect drop = AppearEffect("Drop", start, previous, 30, default, 0, 8);
        AssertVector(start + new Vector2(0, 8), drop.InitialPosition, "Drop initial position");
        RequireTween(drop, "Position", start, 8, "Linear");
        RequireTween(drop, "Scale", 1, 1, "OutQuad");
        Effect rise = AppearEffect("Rise", start, previous, 30, default, 0, 8);
        AssertVector(start - new Vector2(0, 8), rise.InitialPosition, "Rise initial position");
        RequireTween(rise, "Scale", 1, 1, "OutQuad");
    }

    private static void VerifyDisappearTransforms()
    {
        Vector2 current = new(10, 20);
        Vector2 nextRuntime = new(30, 40);
        Effect scatter = DisappearEffect("Scatter", current, nextRuntime, 25, new(3, -2), 60, 4);
        RequireTween(scatter, "Position", current + new Vector2(3, -2), 4, "OutSine");
        RequireTween(scatter, "Rotation", 25 + 60, 4, "OutSine");
        if (scatter.SortingMode != "Back") throw new InvalidOperationException("Scatter did not move to back");

        Effect far = DisappearEffect("Scatter_Far", current, nextRuntime, 25, new(7, -7), -70, 4);
        RequireTween(far, "Position", current + new Vector2(7, -7), 4, "OutSine");
        AssertThrows(() => DisappearEffect("Scatter", current, nextRuntime, 25, new Vector2(0, -4.01f), 0, 4),
            "Scatter accepted a random position outside -4..4");
        AssertThrows(() => DisappearEffect("Scatter_Far", current, nextRuntime, 25, default, -75.01, 4),
            "Scatter_Far accepted a rotation outside -75..75");
        Effect retract = DisappearEffect("Retract", current, nextRuntime, 25, default, 0, 4);
        RequireTween(retract, "Position", nextRuntime, 4, "OutSine");
        RequireTween(retract, "Scale", 0, 4, "OutSine");
        if (retract.SortingOffset != -15) throw new InvalidOperationException("Retract sorting offset was not three layers");
        RequireTween(DisappearEffect("Shrink", current, nextRuntime, 25, default, 0, 4),
            "Scale", 0, 4, "OutQuad");
        Effect spin = DisappearEffect("Shrink_Spin", current, nextRuntime, 25, default, 0, 4);
        RequireTween(spin, "Rotation", 25 - 180, 4, "OutSine");
        RequireTween(DisappearEffect("Fade", current, nextRuntime, 25, default, 0, 4),
            "Opacity", 0, 4, "Linear");
        if (DisappearEffect("Retract", current, null, 25, default, 0, 4).Tweens.Count != 0)
            throw new InvalidOperationException("terminal Retract created a tween without a next floor");
    }

    private static void VerifyPauseAndSeek()
    {
        AssertNear(0, Evaluate(0, 100, 10, 4, 9), "before event");
        AssertNear(50, Evaluate(0, 100, 10, 4, 12), "midpoint");
        AssertNear(100, Evaluate(0, 100, 10, 4, 14), "complete");
        AssertNear(25, Evaluate(0, 100, 10, 4, 11), "backward seek");
        double pausedChartTime = 12;
        AssertNear(50, Evaluate(0, 100, 10, 4, pausedChartTime), "pause midpoint");
        AssertNear(50, Evaluate(0, 100, 10, 4, pausedChartTime), "wall time advanced paused tween");
        AssertNear(75, Evaluate(0, 100, 10, 4, 13), "resume");
    }

    private static void VerifyTweenKeyCompetition()
    {
        string[] animate = ["Position", "Scale", "Rotation", "Opacity"];
        string[] move = ["PositionX", "PositionY", "ScaleX", "ScaleY", "Rotation", "Opacity"];
        AssertTrue(animate.Intersect(move).Order().SequenceEqual(new[] { "Opacity", "Rotation" }),
            "rotation/opacity key overlap changed");
        AssertTrue(!move.Contains("Position") && !move.Contains("Scale"),
            "position/scale must remain parallel key groups");
        AssertNear(100, KillCompleteThenStart(0, 100, 0.5), "Kill(complete:true) did not complete old target");
    }

    private static void VerifyPositionTrackInteraction()
    {
        var baseState = new PositionTrackBase(new Vector2(25, 50), 40, 1.75f);
        Effect growSpin = AppearEffect("Grow_Spin", baseState.StartPosition, default,
            baseState.StartRotation, default, 0, 2);
        RequireTween(growSpin, "Position", baseState.StartPosition, 2, "OutSine");
        RequireTween(growSpin, "Rotation", baseState.StartRotation, 2, "OutSine");
        RequireTween(growSpin, "Scale", 1, 2, "OutQuad");
        AssertTrue(Math.Abs(baseState.StartScale - 1) > Tolerance,
            "fixture must distinguish PositionTrack scale from Animate target");
    }

    private static void VerifySpecialCapabilities()
    {
        RuntimeCapabilities required = new(
            ExtendAnim: true,
            PreviousFloorCurrentPosition: true,
            NextFloorRuntimePosition: true,
            SortingOffset: true,
            CombinedPositionTweenKey: true,
            CombinedScaleTweenKey: true);
        AssertTrue(required is { ExtendAnim: true, PreviousFloorCurrentPosition: true,
            NextFloorRuntimePosition: true, SortingOffset: true }, "special capability fixture is incomplete");
    }

    private static void VerifyCompactSegments()
    {
        const int floorCount = 100_000;
        AnimateChange[] changes =
        [
            new(0, true, "Grow", 3, "Fade", 4),
            new(50_000, true, "Rise", 2, "Shrink", 3),
            new(75_000, false, "Drop", 9, "Retract", 9)
        ];
        AnimateSegment[] segments = BuildSegments(changes);
        AssertTrue(segments.Length == 2, $"expected 2 active segments, actual={segments.Length}");
        AssertState(new("Grow", 3, "Fade", 4), Lookup(segments, 49_999), "segment lookup before switch");
        AssertState(new("Rise", 2, "Shrink", 3), Lookup(segments, floorCount - 1), "segment lookup after switch");
    }

    private static void VerifyProductionPersistentSegments()
    {
        ProductionData actual = ProductionTimeline(8,
        [
            AnimateAction(1, "Grow", 3, "Shrink", 4),
            AnimateAction(3, "Drop", 99, "Retract", 99) with { Active = false },
            SourceAnimateAction(5, "Rise", 2, "Fade", 1)
        ]);
        AssertTrue(actual.Segments.Length == 2, $"expected 2 persistent segments, actual={actual.Segments.Length}");
        AssertSegment(actual.Segments[0], 1, 4,
            NativeTrackAnimationSegment.AppearGrow, NativeTrackAnimationSegment.DisappearShrink, 3, 4);
        AssertSegment(actual.Segments[1], 5, 7,
            NativeTrackAnimationSegment.AppearRise, NativeTrackAnimationSegment.DisappearFade, 2, 1);

        ProductionData partial = ProductionTimeline(8,
        [
            AnimateAction(1, "Grow", 3, "Shrink", 4),
            SetSpeedAction(3, 240),
            DisabledAppearAnimateAction(3, "Drop", 9, "Fade", 2)
        ]);
        AssertTrue(partial.Segments.Length == 2, "partial-disabled AnimateTrack segment count");
        AssertTrue(partial.Segments[1].AppearType == NativeTrackAnimationSegment.AppearGrow,
            "disabled appear property did not preserve persistent state");
        AssertTrue(partial.Segments[1].DisappearType == NativeTrackAnimationSegment.DisappearFade,
            "enabled disappear property did not update persistent state");
        AssertNear(1, partial.Segments[1].AppearReferenceSpeed,
            "disabled-appear branch did not select previous speed reference");
    }

    private static void VerifyProductionNormalTiming()
    {
        ProductionData actual = ProductionTimeline(8, [AnimateAction(2, "Fade", 2, "None", 4)]);
        NativeTrackAnimationSegment segment = SingleSegment(actual);
        NativeTrackAnimationTiming timing = actual.Timings[2];
        AssertNear(0.5, timing.BeatSecondsNoPitch, "managed beat seconds");
        AssertNear(1.5,
            Math.Max(timing.EntryTime - segment.BeatsAhead * timing.BeatSecondsNoPitch, 0),
            "normal timing fixture start");
        AssertNear(0.25, Math.Min(0.5 * timing.BeatSecondsNoPitch / segment.Pitch, 0.5),
            "normal duration");
    }

    private static void VerifyProductionVerticalTiming()
    {
        ProductionData actual = ProductionTimeline(8, [AnimateAction(3, "Drop", 2, "None", 4)]);
        NativeTrackAnimationSegment segment = SingleSegment(actual);
        NativeTrackAnimationTiming timing = actual.Timings[3];
        AssertTrue(segment.AppearType == NativeTrackAnimationSegment.AppearDrop, "Drop type was not preserved");
        AssertNear(1.0,
            Math.Max(timing.EntryTime - 2 * segment.BeatsAhead * timing.BeatSecondsNoPitch, 0),
            "Drop timing fixture start");
        AssertNear(segment.BeatsAhead * timing.BeatSecondsNoPitch / segment.Pitch, 1,
            "Drop duration");
    }

    private static void VerifyProductionDisappearTiming()
    {
        ProductionData actual = ProductionTimeline(8, [AnimateAction(1, "None", 3, "Fade", 2)]);
        NativeTrackAnimationSegment segment = SingleSegment(actual);
        NativeTrackAnimationTiming timing = actual.Timings[1];
        double start = actual.Timings[2].EntryTime + segment.BeatsBehind * timing.BeatSecondsNoPitch;
        AssertNear(3.5, start, "next-floor based disappear start");
        AssertNear(0.25, Math.Min(0.5 * timing.BeatSecondsNoPitch / segment.Pitch, 0.5),
            "disappear duration");
    }

    private static void VerifyProductionAppearTypes()
    {
        string[] names = ["None", "Assemble", "Assemble_Far", "Extend", "Grow", "Grow_Spin", "Fade", "Drop", "Rise"];
        uint[] expected = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        for (int i = 0; i < names.Length; i++)
        {
            NativeTrackAnimationSegment segment = SingleSegment(
                ProductionTimeline(4, [AnimateAction(1, names[i], 3, "None", 4)]));
            AssertTrue(segment.AppearType == expected[i], $"{names[i]} expected={expected[i]} actual={segment.AppearType}");
        }
    }

    private static void VerifyProductionDisappearTypes()
    {
        string[] names = ["None", "Scatter", "Scatter_Far", "Retract", "Shrink", "Shrink_Spin", "Fade"];
        uint[] expected = [0, 1, 2, 3, 4, 5, 6];
        for (int i = 0; i < names.Length; i++)
        {
            NativeTrackAnimationSegment segment = SingleSegment(
                ProductionTimeline(4, [AnimateAction(1, "None", 3, names[i], 4)]));
            AssertTrue(segment.DisappearType == expected[i], $"{names[i]} expected={expected[i]} actual={segment.DisappearType}");
        }
    }

    private static void VerifyProductionChartReconstruction()
    {
        ProductionData first = ProductionTimeline(8, [AnimateAction(1, "Fade", 3, "Fade", 4)]);
        ProductionData second = ProductionTimeline(8, [AnimateAction(1, "Fade", 3, "Fade", 4)]);
        NativeTrackAnimationSegment a = SingleSegment(first);
        NativeTrackAnimationSegment b = SingleSegment(second);
        AssertTrue(a.Equals(b), "timeline compilation was not deterministic");
        AssertTrue(first.Timings.SequenceEqual(second.Timings), "timing compilation was not deterministic");
    }

    private static void VerifyProductionCompetitionRepresentation()
    {
        ProductionData actual = ProductionTimeline(8,
        [
            AnimateAction(1, "Grow_Spin", 3, "Fade", 4),
            MoveAction(1, 2)
        ]);
        AssertTrue(actual.Segments.Length == 1, "Animate combined channel was not compiled");
        AssertTrue(actual.TransformEvents.Length == 1, "MoveTrack axis channel was not retained");
        AssertTrue(actual.TransformEvents[0].Flags != 0, "MoveTrack flags were lost");
    }

    private static void VerifyProductionPositionTrackInteraction()
    {
        ProductionData actual = ProductionTimeline(8,
        [
            PositionAction(1, 0),
            AnimateAction(1, "Grow_Spin", 3, "None", 4)
        ]);
        AssertTrue(actual.Segments.Length == 1, "Animate segment was not retained with PositionTrack");
        AssertTrue(actual.Segments[0].AppearType == NativeTrackAnimationSegment.AppearGrowSpin,
            "Grow_Spin state was not preserved with PositionTrack");
    }

    private static void VerifyProductionSpecialCapabilities()
    {
        ProductionData actual = ProductionTimeline(8,
        [
            AnimateAction(1, "Extend", 3, "Retract", 4)
        ]);
        NativeTrackAnimationSegment segment = SingleSegment(actual);
        AssertTrue(segment.AppearType == NativeTrackAnimationSegment.AppearExtend,
            "Extend runtime type was not preserved");
        AssertTrue(segment.DisappearType == NativeTrackAnimationSegment.DisappearRetract,
            "Retract runtime type was not preserved");
    }

    private static void VerifyProductionCompactSegments()
    {
        ProductionData actual = ProductionTimeline(100_000,
        [
            AnimateAction(0, "Grow", 3, "Fade", 4),
            AnimateAction(50_000, "Rise", 2, "Shrink", 3)
        ]);
        AssertTrue(actual.Segments.Length == 2, $"expected 2 indexed persistent segments, actual={actual.Segments.Length}");
        AssertTrue(actual.Segments[0].EndFloor == 49_999, "first compact segment end mismatch");
        AssertTrue(actual.Segments[1].EndFloor == 99_999, "last compact segment end mismatch");
    }

    private static ProductionData ProductionTimeline(int floors, LevelAction[] actions)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(floors);
        level.InitialBpm = 120;
        level.PitchPercent = 100;
        level.ReplaceActions(actions);
        PreparedNativePlayback prepared = NativeLevelViewport.PreparePlayback(level, TimingMapBuilder.Build(level));
        return new(prepared.TrackAnimationTimeline, prepared.TrackAnimationTimings, prepared.TrackTransformTimeline);
    }

    private static NativeTrackAnimationSegment SingleSegment(ProductionData data)
    {
        AssertTrue(data.Segments.Length == 1, $"expected one segment, actual={data.Segments.Length}");
        return data.Segments[0];
    }

    private static void AssertSegment(
        NativeTrackAnimationSegment actual, int start, int end, uint appear, uint disappear,
        double ahead, double behind)
    {
        AssertTrue(actual.StartFloor == start && actual.EndFloor == end,
            $"segment range expected={start}..{end} actual={actual.StartFloor}..{actual.EndFloor}");
        AssertTrue(actual.AppearType == appear && actual.DisappearType == disappear,
            $"segment types expected={appear}/{disappear} actual={actual.AppearType}/{actual.DisappearType}");
        AssertNear(ahead, actual.BeatsAhead, "segment beatsAhead");
        AssertNear(behind, actual.BeatsBehind, "segment beatsBehind");
    }

    private static LevelAction AnimateAction(
        int floor, string appear, double ahead, string disappear, double behind) =>
        new(floor, "AnimateTrack", true, null, null, null, null)
        {
            SourceIndex = floor + 100,
            PropertyOverrides = new JsonObject
            {
                ["trackAnimation"] = appear,
                ["beatsAhead"] = ahead,
                ["trackDisappearAnimation"] = disappear,
                ["beatsBehind"] = behind
            }
        };

    private static LevelAction SourceAnimateAction(
        int floor, string appear, double ahead, string disappear, double behind) =>
        new(floor, "AnimateTrack", true, null, null, null, null)
        {
            SourceIndex = floor + 200,
            SourceProperties = new JsonObject
            {
                ["trackAnimation"] = appear,
                ["beatsAhead"] = ahead,
                ["trackDisappearAnimation"] = disappear,
                ["beatsBehind"] = behind
            }
        };

    private static LevelAction DisabledAppearAnimateAction(
        int floor, string appear, double ahead, string disappear, double behind) =>
        new(floor, "AnimateTrack", true, null, null, null, null)
        {
            SourceIndex = floor + 300,
            PropertyOverrides = new JsonObject
            {
                ["trackAnimation"] = appear,
                ["beatsAhead"] = ahead,
                ["trackDisappearAnimation"] = disappear,
                ["beatsBehind"] = behind,
                ["disabled"] = new JsonObject { ["trackAnimation"] = true }
            }
        };

    private static LevelAction SetSpeedAction(int floor, double bpm) =>
        new(floor, "SetSpeed", true, "Bpm", bpm, null, null)
        {
            SourceIndex = floor + 250
        };

    private static LevelAction MoveAction(int floor, int sourceIndex) =>
        new(floor, "MoveTrack", true, null, null, 1, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["startTile"] = new JsonArray(0, "ThisTile"),
                ["endTile"] = new JsonArray(0, "ThisTile"),
                ["positionOffset"] = new JsonArray(1, 2),
                ["duration"] = 1,
                ["ease"] = "Linear"
            }
        };

    private static LevelAction PositionAction(int floor, int sourceIndex) =>
        new(floor, "PositionTrack", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["positionOffset"] = new JsonArray(2, 3),
                ["rotation"] = 40,
                ["scale"] = 175
            }
        };

    private static AnimateState[] ResolveStates(int floorCount, IEnumerable<AnimateChange> changes)
    {
        AnimateState current = DefaultState;
        AnimateChange[] active = changes.Where(static item => item.Active).OrderBy(static item => item.Floor).ToArray();
        int cursor = 0;
        var result = new AnimateState[floorCount];
        for (int floor = 0; floor < floorCount; floor++)
        {
            while (cursor < active.Length && active[cursor].Floor == floor)
            {
                AnimateChange item = active[cursor++];
                current = new(item.Appear, item.BeatsAhead, item.Disappear, item.BeatsBehind);
            }
            result[floor] = current;
        }
        return result;
    }

    private static Timing AppearTiming(
        string animation, double entryTime, double bpm, double speed, double pitch, double beats)
    {
        double beatSeconds = 60 / (bpm * speed);
        bool vertical = animation is "Drop" or "Rise";
        return new(
            Math.Max(entryTime - beats * beatSeconds * (vertical ? 2 : 1), 0),
            vertical ? beats * beatSeconds / pitch : Math.Min(0.5 * beatSeconds / pitch, 0.5));
    }

    private static Timing? DisappearTiming(
        double entryTime, double nextEntryTime, double bpm, double speed,
        double pitch, double beats, bool hasNext)
    {
        _ = entryTime;
        if (!hasNext) return null;
        double beatSeconds = 60 / (bpm * speed);
        return new(nextEntryTime + beats * beatSeconds, Math.Min(0.5 * beatSeconds / pitch, 0.5));
    }

    private static double EffectiveBeats(double configured, double floorSpeed, double referenceSpeed) =>
        configured * floorSpeed / referenceSpeed;

    private static double SelectCorrectedBeats(
        double configured, double floorSpeed, double currentReference,
        double previousReference, bool appearEnabled) =>
        EffectiveBeats(configured, floorSpeed, appearEnabled ? currentReference : previousReference);

    private static Effect AppearEffect(
        string type, Vector2 start, Vector2 previous, double startRotation,
        Vector2 randomPosition, double randomRotation, double duration)
    {
        var result = new Effect(start, startRotation, 1, 1, -1, null, 0, []);
        if (type == "None") return result;
        result.Tweens.Add(new("Position", start, duration, type is "Drop" or "Rise" ? "Linear" : "OutSine"));
        switch (type)
        {
            case "Assemble":
            case "Assemble_Far":
                ValidateRandom(randomPosition, randomRotation, type == "Assemble" ? 4 : 8);
                result.InitialPosition = start + randomPosition;
                result.InitialRotation = startRotation + randomRotation;
                result.Tweens.Add(new("Rotation", startRotation, duration, "OutSine"));
                break;
            case "Extend":
                result.InitialPosition = previous;
                result.InitialScale = 0;
                result.ExtendAnim = 0;
                result.Tweens.Add(new("Scale", 1d, duration, "OutSine"));
                result.Tweens.Add(new("ExtendAnim", 1d, duration, "OutSine"));
                break;
            case "Grow":
                result.InitialScale = 0;
                result.Tweens.Add(new("Scale", 1d, duration, "OutQuad"));
                break;
            case "Grow_Spin":
                result.InitialScale = 0;
                result.InitialRotation = startRotation - 180;
                result.Tweens.Add(new("Scale", 1d, duration, "OutQuad"));
                result.Tweens.Add(new("Rotation", startRotation, duration, "OutSine"));
                break;
            case "Fade":
                result.InitialOpacity = 0;
                result.Tweens.Add(new("Opacity", 1d, duration, "Linear"));
                break;
            case "Drop":
            case "Rise":
                result.InitialPosition = start + new Vector2(0, type == "Drop" ? 8 : -8);
                result.InitialScale = 0;
                result.Tweens.Add(new("Scale", 1d, duration / 8, "OutQuad"));
                break;
        }
        return result;
    }

    private static Effect DisappearEffect(
        string type, Vector2 current, Vector2? nextRuntime, double startRotation,
        Vector2 randomPosition, double randomRotation, double duration)
    {
        var result = new Effect(current, startRotation, 1, 1, -1, null, 0, []);
        switch (type)
        {
            case "Scatter":
            case "Scatter_Far":
                ValidateRandom(randomPosition, randomRotation, type == "Scatter" ? 4 : 8);
                result.SortingMode = "Back";
                result.Tweens.Add(new("Position", current + randomPosition, duration, "OutSine"));
                result.Tweens.Add(new("Rotation", startRotation + randomRotation, duration, "OutSine"));
                break;
            case "Retract":
                if (nextRuntime is null)
                    break;
                result.SortingOffset = -15;
                result.Tweens.Add(new("Position", nextRuntime.Value, duration, "OutSine"));
                result.Tweens.Add(new("Scale", 0d, duration, "OutSine"));
                break;
            case "Shrink":
                result.Tweens.Add(new("Scale", 0d, duration, "OutQuad"));
                break;
            case "Shrink_Spin":
                result.Tweens.Add(new("Scale", 0d, duration, "OutQuad"));
                result.Tweens.Add(new("Rotation", startRotation - 180, duration, "OutSine"));
                break;
            case "Fade":
                result.Tweens.Add(new("Opacity", 0d, duration, "Linear"));
                break;
        }
        return result;
    }

    private static void ValidateRandom(Vector2 position, double rotation, double positionLimit)
    {
        if (position.X < -positionLimit || position.X > positionLimit ||
            position.Y < -positionLimit || position.Y > positionLimit ||
            rotation < -75 || rotation > 75)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
    }

    private static double Evaluate(double start, double target, double eventStart, double duration, double chartTime)
    {
        if (chartTime < eventStart) return start;
        double progress = duration <= 0 ? 1 : Math.Clamp((chartTime - eventStart) / duration, 0, 1);
        return start + (target - start) * progress;
    }

    private static double KillCompleteThenStart(double current, double oldTarget, double progress)
    {
        _ = current;
        _ = progress;
        return oldTarget;
    }

    private static AnimateSegment[] BuildSegments(IEnumerable<AnimateChange> changes) =>
        changes.Where(static item => item.Active)
            .OrderBy(static item => item.Floor)
            .Select(static item => new AnimateSegment(
                item.Floor, new AnimateState(item.Appear, item.BeatsAhead, item.Disappear, item.BeatsBehind)))
            .ToArray();

    private static AnimateState Lookup(AnimateSegment[] segments, int floor)
    {
        AnimateState state = DefaultState;
        foreach (AnimateSegment item in segments)
        {
            if (item.StartFloor > floor) break;
            state = item.State;
        }
        return state;
    }

    private static void RequireTween(Effect effect, string property, object target, double duration, string ease)
    {
        TweenSpec? tween = effect.Tweens.LastOrDefault(item => item.Property == property);
        if (tween is null)
            throw new InvalidOperationException($"missing {property} tween");
        if (tween.Target is Vector2 vector && target is Vector2 wanted)
            AssertVector(wanted, vector, $"{property} target");
        else
            AssertNear(Convert.ToDouble(target), Convert.ToDouble(tween.Target), $"{property} target");
        AssertNear(duration, tween.Duration, $"{property} duration");
        if (tween.Ease != ease)
            throw new InvalidOperationException($"{property} ease expected={ease} actual={tween.Ease}");
    }

    private static void AssertState(AnimateState expected, AnimateState actual, string phase)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{phase}: expected={expected} actual={actual}");
    }

    private static void AssertVector(Vector2 expected, Vector2 actual, string phase)
    {
        if (Vector2.Distance(expected, actual) > Tolerance)
            throw new InvalidOperationException($"{phase}: expected={expected} actual={actual}");
    }

    private static void AssertNear(double expected, double actual, string phase)
    {
        if (Math.Abs(expected - actual) > Tolerance)
            throw new InvalidOperationException($"{phase}: expected={expected} actual={actual}");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static readonly AnimateState DefaultState = new("None", 3, "None", 4);

    private sealed record AnimateChange(
        int Floor, bool Active, string Appear, double BeatsAhead, string Disappear, double BeatsBehind);
    private sealed record AnimateState(string Appear, double BeatsAhead, string Disappear, double BeatsBehind);
    private sealed record AnimateSegment(int StartFloor, AnimateState State);
    private readonly record struct Timing(double Start, double Duration);
    private sealed record PositionTrackBase(Vector2 StartPosition, double StartRotation, double StartScale);
    private sealed record ProductionData(
        NativeTrackAnimationSegment[] Segments,
        NativeTrackAnimationTiming[] Timings,
        NativeTrackTransformEvent[] TransformEvents);
    private sealed record RuntimeCapabilities(
        bool ExtendAnim,
        bool PreviousFloorCurrentPosition,
        bool NextFloorRuntimePosition,
        bool SortingOffset,
        bool CombinedPositionTweenKey,
        bool CombinedScaleTweenKey);
    private sealed record TweenSpec(string Property, object Target, double Duration, string Ease);
    private sealed record Effect(
        Vector2 InitialPosition,
        double InitialRotation,
        double InitialScale,
        double InitialOpacity,
        double ExtendAnim,
        string? SortingMode,
        int SortingOffset,
        List<TweenSpec> Tweens)
    {
        public Vector2 InitialPosition { get; set; } = InitialPosition;
        public double InitialRotation { get; set; } = InitialRotation;
        public double InitialScale { get; set; } = InitialScale;
        public double InitialOpacity { get; set; } = InitialOpacity;
        public double ExtendAnim { get; set; } = ExtendAnim;
        public string? SortingMode { get; set; } = SortingMode;
        public int SortingOffset { get; set; } = SortingOffset;
    }
}
