using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Edited independently-owned floor-aligned Segments are now a NEW candidate
/// composition, not required to equal the unedited source template. All tests
/// are in-memory, isolated from any Git repository or actual .adofai writer.
/// </summary>
internal static class SegmentEditedCompositionRegression
{
    private const int SampleRate = 48_000;

    public static void Run()
    {
        LevelDocument template = Chart();
        LevelSegment[] baseline = LevelSegmentPartitioner.Split(template, 3, 6).ToArray();
        var edited = EditedSegments(baseline);

        // The test intentionally computes sample allocations from the
        // resulting edited COMPLETE chart. This is not a promise that the
        // original work allocation can survive a tempo/angle change.
        LevelDocument independentlyJoined =
            LevelSegmentPartitioner.Merge(template,
                new[] { edited["A"], edited["B"], edited["C"] });
        SegmentTimeTimeline aligned = Allocations(independentlyJoined);

        string originalTemplate = JsonSerializer.Serialize(
            new
            {
                template.Angles,
                Actions = template.ActionStore.Actions,
                Decorations = template.Decorations,
                template.Positions
            });
        var originalPositions = template.Positions.ToArray();
        string originalA = JsonSerializer.Serialize(edited["A"]);
        string originalB = JsonSerializer.Serialize(edited["B"]);
        string originalC = JsonSerializer.Serialize(edited["C"]);
        string originalAllocations = JsonSerializer.Serialize(aligned);

        SegmentEditedCompositionResult result =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, aligned, edited);

        Check(result.Accepted &&
              result.Document is not null && result.Timing is not null &&
              result.Issues.Count == 0,
            "legitimate independently edited Segments are a valid modeled composition");

        LevelDocument chart = result.Document!;
        TimingMap timing = result.Timing!;
        Equal(template.FloorCount, chart.FloorCount,
            "floor count preserved through independent edits");
        Check(!template.Angles.SequenceEqual(chart.Angles),
            "valid edited angles need not match original chart");
        Check(!TimingMapBuilder.Build(template).Floors
              .Select(f => f.ExitTime)
              .SequenceEqual(timing.Floors.Select(f => f.ExitTime)),
            "valid new composition may have different global timing");
        Equal(30d, chart.Angles[1], "A angle survives");
        Equal(120d, chart.Angles[3], "B angle survives");
        Equal(60d, chart.Angles[5], "C angle survives");

        LevelAction speedA = chart.ActionStore.Actions.Single(
            a => a.Floor == 1 && a.EventType == "SetSpeed");
        Equal(1.5, speedA.BpmMultiplier, "A speed multiplier preserved");
        LevelAction pauseB = chart.ActionStore.Actions.Single(
            a => a.Floor == 4 && a.EventType == "Pause");
        Equal(1.5, pauseB.Duration.GetValueOrDefault(), "B pause duration preserved");
        LevelAction speedC = chart.ActionStore.Actions.Single(
            a => a.Floor == 6 && a.EventType == "SetSpeed");
        Equal(200d, speedC.BeatsPerMinute.GetValueOrDefault(), "C BPM preserved");
        Check(chart.ActionStore.Actions.Any(
            a => a.Floor == 6 && a.EventType == "Twirl"),
            "new Twirl authored within C survives composition");
        Check(chart.Decorations.Single(d => d.Floor == 5)
                .Properties["extra"]?["revision"]?.ToString() == "B-edited",
            "B's nested decoration metadata is not lost");

        for (int floor = 0; floor < chart.FloorCount; floor++)
        {
            FloorTiming expected = TimingMapBuilder.Build(chart).Floors[floor];
            FloorTiming actual = timing.Floors[floor];
            Near(expected.EntryTime, actual.EntryTime,
                "globally accumulated entry time at floor " + floor);
            Near(expected.ExitTime, actual.ExitTime,
                "globally accumulated exit time at floor " + floor);
            Equal(expected.IsCcw, actual.IsCcw, "global Twirl state");
            Near(expected.Bpm, actual.Bpm, "global effective BPM");
        }
        Equal(originalTemplate, JsonSerializer.Serialize(
            new
            {
                template.Angles,
                Actions = template.ActionStore.Actions,
                Decorations = template.Decorations,
                template.Positions
            }), "the template must remain untouched on success");
        Check(originalPositions.SequenceEqual(template.Positions),
            "template world positions must remain untouched on success");
        Equal(originalA, JsonSerializer.Serialize(edited["A"]),
            "A content untouched on success");
        Equal(originalB, JsonSerializer.Serialize(edited["B"]),
            "B content untouched on success");
        Equal(originalC, JsonSerializer.Serialize(edited["C"]),
            "C content untouched on success");
        Equal(originalAllocations, JsonSerializer.Serialize(aligned),
            "time ownership unchanged on success");

        // Current ownership ranges are tied to decoded audio samples.
        // A tempo change in A shifts later sample boundaries. Silently
        // keeping the OLD time allocations must be rejected.
        LevelDocument oldChart = LevelSegmentPartitioner.Merge(template, baseline);
        SegmentTimeTimeline obsolete = Allocations(oldChart);
        Check(!JsonSerializer.Serialize(obsolete).Equals(originalAllocations,
                StringComparison.Ordinal),
            "fixture actually changed absolute sample boundaries");
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, obsolete, edited),
            SegmentEditedCompositionIssueKind.SampleRangeMismatch,
            "old allocations no longer fit an edited global tempo");

        SegmentTimePartition bAllocation = aligned.Partitions.Single(
            p => p.SegmentId == "B");
        var displaced = aligned with
        {
            Partitions = aligned.Partitions.Select(part =>
                part.SegmentId == "B"
                    ? part with { StartSample = part.StartSample + 24 }
                    : part).ToArray()
        };
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, displaced, edited),
            SegmentEditedCompositionIssueKind.SampleRangeMismatch,
            "a sample-gap at B's floor boundary is not silently accepted");
        Equal(bAllocation.StartSample,
            aligned.Partitions.Single(p => p.SegmentId == "B").StartSample,
            "rejecting shifted samples must not mutate assignments");

        var missingB = edited.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        missingB.Remove("B");
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, aligned, missingB),
            SegmentEditedCompositionIssueKind.MissingActiveContent,
            "missing Segment B payload must not result in partial chart");

        var badFloor = edited.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        badFloor["B"] = badFloor["B"] with { StartFloor = 4 };
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, aligned, badFloor),
            SegmentEditedCompositionIssueKind.InvalidFloorCoverage,
            "overlapping/missing floor coverage is rejected, not patched");

        // The sample allocator can retain parked revisions without using
        // them in the assembled playable candidate.
        var parked = aligned with
        {
            Partitions = aligned.Partitions.Concat(
            [
                new SegmentTimePartition(
                    "P", 0, 200, "parked-r1", 0,
                    null, SegmentPartitionStatus.Parked)
            ]).ToArray()
        };
        var includingParked = edited.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        includingParked["P"] = baseline[0];
        string parkedBytes = JsonSerializer.Serialize(includingParked["P"]);
        SegmentEditedCompositionResult withoutParked =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, parked, includingParked);
        Check(withoutParked.Accepted && withoutParked.Document is not null,
            "parked revisions do not displace active chart content");
        Equal(parkedBytes, JsonSerializer.Serialize(includingParked["P"]),
            "parked payload is retained without mutation");
        Equal(30d, withoutParked.Document!.Angles[1],
            "parked content never silently overwrites active A");

        // An event whose exact behavior across a cut is not represented by
        // the current loader/timing/pose model must fail closed.
        ExpectUnsupported(template, edited, aligned,
            new LevelAction(2, "ScaleRadius", true, null, null, null, null),
            "ScaleRadius affects world geometry after A's end");
        ExpectUnsupported(template, edited, aligned,
            new LevelAction(4, "PositionTrack", true, null, null, null, null),
            "PositionTrack affects later segment positions");
        ExpectUnsupported(template, edited, aligned,
            new LevelAction(2, "MoveDecorations", true, null, null, null, null)
            {
                Duration = 4,
                SourceProperties = new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = "MoveDecorations",
                    ["duration"] = 4,
                    ["tag"] = "fx"
                }
            },
            "unsupported duration effect could cross audio ownership");

        // An active ownership slice with no matching floor-aligned Segment
        // must not cause the validator to invent a tile or split through one.
        var extraPart = aligned with
        {
            Partitions = aligned.Partitions.Concat(
            [
                new SegmentTimePartition(
                    "D", aligned.Partitions.Max(p => p.EndSampleExclusive) + 1,
                    aligned.Partitions.Max(p => p.EndSampleExclusive) + 101,
                    "D-r1", aligned.Partitions.Max(p => p.EndSampleExclusive) + 1,
                    "author-D", SegmentPartitionStatus.Active)
            ]).ToArray()
        };
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, extraPart, edited),
            SegmentEditedCompositionIssueKind.MissingActiveContent,
            "an unrepresented active segment cannot be invented");

        Equal(originalA, JsonSerializer.Serialize(edited["A"]),
            "A edit survives every rejected validation");
        Equal(originalB, JsonSerializer.Serialize(edited["B"]),
            "B edit survives every rejected validation");
        Equal(originalC, JsonSerializer.Serialize(edited["C"]),
            "C edit survives every rejected validation");
        Equal(originalAllocations, JsonSerializer.Serialize(aligned),
            "no validation failure changed the shared audio allocations");
    }

    private static LevelDocument Chart()
    {
        LevelDocument template = LevelDocument.CreateSynthetic(9);
        template.Angles = [0d, 45d, 999d, 90d, 270d, 15d, 120d, 315d];
        template.RebuildGeometry();
        template.InitialBpm = 120d;
        template.OffsetMilliseconds = 150d;
        template.PitchPercent = 90d;
        template.CountdownTicks = 0;
        template.SeparateCountdownTime = false;
        template.ReplaceActions(
        [
            new LevelAction(1, "SetSpeed", true, "Multiplier", null, 1.25, null),
            new LevelAction(2, "Twirl", true, null, null, null, null),
            new LevelAction(4, "Pause", true, null, null, null, null)
            {
                Duration = 1.0
            },
            new LevelAction(6, "SetSpeed", true, "Bpm", 180, null, null)
        ]);
        template.ReplaceDecorations(
        [
            new LevelDecoration(5, "AddDecoration")
            {
                SourceIndex = 1,
                Properties = new JsonObject
                {
                    ["decorationImage"] = "sprite.png",
                    ["extra"] = new JsonObject { ["revision"] = "base" }
                }
            }
        ]);
        return template;
    }

    private static Dictionary<string, LevelSegment> EditedSegments(LevelSegment[] baseline)
    {
        LevelSegment originalA = baseline[0];
        double[] anglesA = originalA.DestinationAngles.ToArray();
        anglesA[1] = 30d;
        LevelAction[] actionsA = originalA.Actions.Select(a =>
            a.EventType == "SetSpeed"
                ? a with { BpmMultiplier = 1.5 }
                : a).ToArray();

        LevelSegment originalB = baseline[1];
        double[] anglesB = originalB.DestinationAngles.ToArray();
        anglesB[1] = 120d;
        LevelAction[] actionsB = originalB.Actions.Select(a =>
            a.EventType == "Pause" ? a with { Duration = 1.5 } : a).ToArray();
        LevelDecoration[] decorationsB = originalB.Decorations.Select(d =>
        {
            JsonObject props = (JsonObject)d.Properties.DeepClone();
            props["extra"]!["revision"] = "B-edited";
            return d with { Properties = props };
        }).ToArray();

        LevelSegment originalC = baseline[2];
        double[] anglesC = originalC.DestinationAngles.ToArray();
        anglesC[0] = 60d;
        LevelAction[] actionsC = originalC.Actions.Select(a =>
            a.EventType == "SetSpeed" ? a with { BeatsPerMinute = 200d } : a)
            .Append(new LevelAction(0, "Twirl", true, null, null, null, null))
            .ToArray();

        return new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
        {
            ["A"] = originalA with { DestinationAngles = anglesA, Actions = actionsA },
            ["B"] = originalB with
            {
                DestinationAngles = anglesB,
                Actions = actionsB,
                Decorations = decorationsB
            },
            ["C"] = originalC with { DestinationAngles = anglesC, Actions = actionsC }
        };
    }

    private static SegmentTimeTimeline Allocations(LevelDocument chart)
    {
        TimingMap timing = TimingMapBuilder.Build(chart);
        (string Id, int Start, int End)[] regions =
        [
            ("A", 0, 3),
            ("B", 3, 6),
            ("C", 6, chart.FloorCount)
        ];
        return new SegmentTimeTimeline(SampleRate, regions.Select(region =>
        {
            long first = EntrySample(chart, timing, region.Start);
            long last = region.End < chart.FloorCount
                ? EntrySample(chart, timing, region.End)
                : SecondsToSamples(PlaybackClock.ChartToAudioTime(chart, timing.Duration));
            return new SegmentTimePartition(
                region.Id, first, last, region.Id + "-r2", first,
                "author-" + region.Id, SegmentPartitionStatus.Active);
        }).ToArray());
    }

    private static long EntrySample(LevelDocument chart, TimingMap timing, int floor) =>
        SecondsToSamples(PlaybackClock.ChartToAudioTime(
            chart, timing.GetEntryTime(floor)));

    private static long SecondsToSamples(double seconds) =>
        checked((long)Math.Round(seconds * SampleRate,
            MidpointRounding.AwayFromZero));

    private static void ExpectUnsupported(
        LevelDocument template, IReadOnlyDictionary<string, LevelSegment> original,
        SegmentTimeTimeline allocations, LevelAction globalAction, string label)
    {
        var edited = original.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        string segmentId = globalAction.Floor < 3 ? "A"
            : globalAction.Floor < 6 ? "B" : "C";
        LevelSegment segment = edited[segmentId];
        int localFloor = globalAction.Floor - segment.StartFloor;
        edited[segmentId] = segment with
        {
            Actions = segment.Actions.Append(
                globalAction with { Floor = localFloor }).ToArray()
        };
        string originalBytes = JsonSerializer.Serialize(edited[segmentId]);
        ExpectIssue(
            SegmentEditedCompositionValidator.ComposeAndValidate(
                template, allocations, edited),
            SegmentEditedCompositionIssueKind.UnsupportedCrossBoundaryEffect,
            label);
        Equal(originalBytes, JsonSerializer.Serialize(edited[segmentId]),
            "unsupported event payload is not modified");
    }

    private static void ExpectIssue(
        SegmentEditedCompositionResult result,
        SegmentEditedCompositionIssueKind kind,
        string description)
    {
        if (result.Accepted || result.Document is not null ||
            result.Timing is not null ||
            !result.Issues.Any(issue => issue.Kind == kind))
            throw new InvalidOperationException(
                $"Expected {kind} with no candidate: {description}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Near(double expected, double actual, string description)
    {
        if (Math.Abs(expected - actual) > 1e-7)
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }
}
