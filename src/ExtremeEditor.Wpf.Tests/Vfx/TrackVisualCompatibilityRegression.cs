using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TrackVisualCompatibilityRegression
{
    private const float Tolerance = 0.0001f;

    public static void Run()
    {
        var failures = new List<string>();
        Check(failures, "Pitch 200% shortens animation duration", VerifyPitch200);
        Check(failures, "Pitch 50% lengthens animation duration", VerifyPitch50);
        Check(failures, "SetSpeed does not alter pitch adjustment", VerifyPitchIndependentOfSetSpeed);
        Check(failures, "Future Recolor is not statically baked", VerifyFutureRecolorIsRuntimeOnly);
        Check(failures, "Recolor gap zero covers inclusive range", VerifyContiguousInclusiveRange);
        Check(failures, "Recolor reversed range and gap are inclusive", VerifyRangeAndGap);
        Check(failures, "Recolor Stripes uses resolved range start", VerifyStripesRangeStart);
        Check(failures, "Recolor runtime metadata survives VFX normalization", VerifyRuntimeMetadata);
        Check(failures, "Recolor RepeatEvents expands in managed timeline", VerifyRepeatExpansion);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Track VFX compatibility regressions failed ({failures.Count}):\n" +
                string.Join("\n", failures.Select(static failure => $"- {failure}")));
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

    private static void VerifyPitch200() => VerifyPitchDuration(200.0, 1.0f, includeSetSpeed: false);

    private static void VerifyPitch50() => VerifyPitchDuration(50.0, 4.0f, includeSetSpeed: false);

    private static void VerifyPitchIndependentOfSetSpeed() =>
        VerifyPitchDuration(200.0, 1.0f, includeSetSpeed: true);

    private static void VerifyPitchDuration(double pitchPercent, float expected, bool includeSetSpeed)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.PitchPercent = pitchPercent;
        var actions = new List<LevelAction>();
        if (includeSetSpeed)
        {
            actions.Add(new LevelAction(1, "SetSpeed", true, "Bpm", 333.0, null, null)
            {
                SourceIndex = 0,
                Kind = LevelActionKind.SetSpeed
            });
        }
        level.ReplaceActions(actions);
        TrackVisualMetadataCache.Attach(level, new TrackVisualSourceData(
            InitialStyle("Single", "ff0000", "0000ff", 2.0, "None", 4, 0),
            []));

        NativeTrackVisual[] result = TrackVisualResolver.Resolve(level);
        AssertNear(expected, result[0].AnimDuration,
            $"expected effective duration {expected}, actual {result[0].AnimDuration}");
    }

    private static void VerifyFutureRecolorIsRuntimeOnly()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        level.ReplaceActions([Action(3, 10, "RecolorTrack")]);
        TrackVisualMetadataCache.Attach(level, new TrackVisualSourceData(
            InitialStyle("Single", "ff0000", "ffffff", 2.0, "None", 4, 0),
            [SourceEvent(10, 3, "RecolorTrack", "Single", "0000ff", "ffffff", start: null, end: null)]));

        NativeTrackVisual[] result = TrackVisualResolver.Resolve(level);
        const uint expectedRed = 0xFF0000FFu;
        uint actual = result[3].PrimaryColor;
        if (actual != expectedRed)
        {
            throw new InvalidOperationException(
                $"future Recolor became base state; expected=0x{expectedRed:X8} actual=0x{actual:X8}");
        }
    }

    private static void VerifyRangeAndGap()
    {
        TrackVisualSourceEvent source = SourceEvent(
            20, 3, "RecolorTrack", "Single", "0000ff", "ffffff",
            start: new TrackTileReference(5, "Start"),
            end: new TrackTileReference(1, "Start"),
            gapLength: 1);
        int[] actual = TrackVisualResolver.ResolveRecolorRange(source, 7).Floors().ToArray();
        AssertSequence([1, 3, 5], actual, "reversed range/gap selection");
    }

    private static void VerifyContiguousInclusiveRange()
    {
        TrackVisualSourceEvent source = SourceEvent(
            19, 2, "RecolorTrack", "Single", "0000ff", "ffffff",
            start: new TrackTileReference(1, "Start"),
            end: new TrackTileReference(4, "Start"),
            gapLength: 0);
        int[] actual = TrackVisualResolver.ResolveRecolorRange(source, 6).Floors().ToArray();
        AssertSequence([1, 2, 3, 4], actual, "inclusive gap=0 selection");
    }

    private static void VerifyStripesRangeStart()
    {
        TrackVisualSourceEvent source = SourceEvent(
            30, 4, "RecolorTrack", "Stripes", "ff0000", "0000ff",
            start: new TrackTileReference(1, "Start"),
            end: new TrackTileReference(5, "Start"));
        int actual = TrackVisualResolver.ResolveRecolorRange(source, 8).Start;
        if (actual != 1)
        {
            throw new InvalidOperationException(
                $"expected resolved range start=1, actual event-floor start={actual}");
        }
    }

    private static void VerifyRuntimeMetadata()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        var properties = new JsonObject
        {
            ["ease"] = "InOutSine",
            ["eventTag"] = "recolor",
            ["startTile"] = new JsonArray(1, "Start"),
            ["endTile"] = new JsonArray(4, "Start"),
            ["gapLength"] = 1
        };
        var recolor = new LevelAction(2, "RecolorTrack", true, null, null, null, null)
        {
            SourceIndex = 40,
            AngleOffset = 90,
            Duration = 2.5,
            EventTag = "recolor",
            PropertyOverrides = properties
        };
        level.ReplaceActions([recolor]);

        VfxOccurrence occurrence = VfxTimelineBuilder.Build(level).Occurrences.Single();
        if (occurrence.EventType != "RecolorTrack" ||
            occurrence.Ease != "InOutSine" ||
            occurrence.Duration != 2.5 ||
            occurrence.StartTime <= 0 ||
            occurrence.SourceEvent.PropertyOverrides?["startTile"] is not JsonArray ||
            occurrence.SourceEvent.PropertyOverrides?["endTile"] is not JsonArray)
        {
            throw new InvalidOperationException(
                "Recolor occurrence lost startTime/duration/ease/range metadata needed by runtime evaluation.");
        }
    }

    private static void VerifyRepeatExpansion()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        var recolor = new LevelAction(1, "RecolorTrack", true, null, null, null, null)
        {
            SourceIndex = 50,
            EventTag = "pulse",
            PropertyOverrides = new JsonObject
            {
                ["eventTag"] = "pulse",
                ["ease"] = "Linear"
            }
        };
        var repeat = new LevelAction(1, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = 51,
            PropertyOverrides = new JsonObject
            {
                ["repeatType"] = "Beat",
                ["tag"] = "pulse",
                ["repetitions"] = 2,
                ["interval"] = 0.5,
                ["executeOnCurrentFloor"] = false
            }
        };
        level.ReplaceActions([recolor, repeat]);

        VfxOccurrence[] occurrences = VfxTimelineBuilder.Build(level).Occurrences
            .Where(static item => item.SourceIndex == 50)
            .ToArray();
        if (occurrences.Length != 3 || occurrences.Count(static item => item.IsRepeated) != 2)
        {
            throw new InvalidOperationException(
                $"expected original + 2 managed repeat occurrences, actual={occurrences.Length}");
        }
    }

    private static TrackVisualStyle InitialStyle(
        string type,
        string primary,
        string secondary,
        double duration,
        string pulse,
        int pulseLength,
        int startFloor) => new(
            type, primary, secondary, duration, pulse, pulseLength,
            "Standard", 100.0, string.Empty, 1.0, startFloor);

    private static TrackVisualSourceEvent SourceEvent(
        int sourceIndex,
        int floor,
        string eventType,
        string type,
        string primary,
        string secondary,
        TrackTileReference? start,
        TrackTileReference? end,
        int gapLength = 0) => new(
            sourceIndex, floor, eventType, true,
            type, primary, secondary, 2.0, "None", 4,
            "Standard", 100.0, string.Empty, 1.0,
            false, gapLength, start, end);

    private static LevelAction Action(int floor, int sourceIndex, string eventType) =>
        new(floor, eventType, true, null, null, null, null)
        {
            SourceIndex = sourceIndex
        };

    private static void AssertNear(float expected, float actual, string message)
    {
        if (Math.Abs(expected - actual) > Tolerance)
            throw new InvalidOperationException(message);
    }

    private static void AssertSequence(int[] expected, int[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message}; expected=[{string.Join(',', expected)}] actual=[{string.Join(',', actual)}]");
        }
    }
}
