using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// RED for time-first empty Segment payloads.
/// No GUI, Git, chart exporter, or source-repository mutation.
/// </summary>
internal static class SegmentEmptyContentRegression
{
    private const int SampleRate = 48_000;
    private static long Sample(long seconds) => checked(seconds * SampleRate);

    public static void Run()
    {
        // The middle music section has ZERO existing tiles.
        // Its assignment is still a fully valid independent Segment.
        var timeline = new SegmentTimeTimeline(SampleRate,
        [
            new("intro", Sample(0), Sample(40), "intro-r1", Sample(0),
                "A", SegmentPartitionStatus.Active),
            new("drop", Sample(40), Sample(80), null, Sample(40),
                "B", SegmentPartitionStatus.Active),
            new("outro", Sample(80), Sample(120), "outro-r1", Sample(80),
                "C", SegmentPartitionStatus.Active)
        ]);

        SegmentTimePlanner.Validate(timeline);
        LevelDocument originalChart = LevelDocument.CreateSynthetic(7);
        LevelSegment[] existing = LevelSegmentPartitioner.Split(
            originalChart, 3).ToArray();
        string timelineBefore = JsonSerializer.Serialize(timeline);
        string chartBefore = JsonSerializer.Serialize(new
        {
            originalChart.Angles,
            Actions = originalChart.ActionStore.Actions,
            originalChart.Decorations,
            originalChart.Positions
        });
        string originalIntro = JsonSerializer.Serialize(existing[0]);
        string originalOutro = JsonSerializer.Serialize(existing[1]);

        // Empty is not represented by a fake floor or a null lookup result.
        SegmentTimePartition drop = timeline.Partitions.Single(
            p => p.SegmentId == "drop");
        SegmentContentEntry empty = SegmentContentPlanner.CreateEmpty(drop);
        Check(empty.SegmentId == "drop", "an empty slot keeps the assigned Segment ID");
        Check(empty.Kind == SegmentContentKind.Empty,
            "the new slot explicitly represents an empty Segment");
        Check(empty.RevisionId is null,
            "new empty Segment does not refer to a nonexistent revision");
        Check(empty.FloorAlignedContent is null,
            "empty Segment must not invent a tile, floor, or anchor");

        var entries = new Dictionary<string, SegmentContentEntry>(StringComparer.Ordinal)
        {
            ["intro"] = new("intro", SegmentContentKind.FloorAligned,
                "intro-r1", existing[0]),
            ["drop"] = empty,
            ["outro"] = new("outro", SegmentContentKind.FloorAligned,
                "outro-r1", existing[1])
        };
        string entriesBefore = JsonSerializer.Serialize(entries);

        SegmentContentValidationResult accepted =
            SegmentContentPlanner.Validate(timeline, entries);
        Check(accepted.Accepted && accepted.Issues.Count == 0,
            "explicitly empty B between authored A/C is a valid draft project");
        Check(entries["drop"].FloorAlignedContent is null,
            "validating must not materialize a phantom tile");

        var missing = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal);
        missing.Remove("drop");
        ExpectIssue(SegmentContentPlanner.Validate(timeline, missing),
            SegmentContentIssueKind.MissingActiveEntry, "drop",
            "missing content is NOT equivalent to an explicit empty Segment");

        var nullEntry = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal);
        nullEntry["drop"] = null!;
        ExpectIssue(SegmentContentPlanner.Validate(timeline, nullEntry),
            SegmentContentIssueKind.InvalidContentEntry, "drop",
            "a null entry is NOT a deliberately empty Segment");

        var wrongRevision = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["outro"] = entries["outro"] with { RevisionId = "some-other-revision" }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, wrongRevision),
            SegmentContentIssueKind.RevisionMismatch, "outro",
            "authored revision identity must match the time allocation");

        var fakeEmpty = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["intro"] = empty with { SegmentId = "intro" }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, fakeEmpty),
            SegmentContentIssueKind.RevisionMismatch, "intro",
            "a previously-authored revision cannot silently be declared empty");

        var inventedTile = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["drop"] = empty with { FloorAlignedContent = existing[0] }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, inventedTile),
            SegmentContentIssueKind.InvalidContentEntry, "drop",
            "empty marker must not contain a hidden floor-aligned payload");

        var missingAuthored = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["outro"] = entries["outro"] with { FloorAlignedContent = null }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, missingAuthored),
            SegmentContentIssueKind.InvalidContentEntry, "outro",
            "authored floor-based payload must actually exist");

        var unknown = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["unallocated"] = empty with { SegmentId = "unallocated" }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, unknown),
            SegmentContentIssueKind.UnknownAllocation, "unallocated",
            "unallocated content cannot silently join a project");

        var wrongId = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["drop"] = empty with { SegmentId = "outro" }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, wrongId),
            SegmentContentIssueKind.InvalidContentEntry, "drop",
            "content entry ID must match the dictionary key and allocation");

        var malformedFloor = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["intro"] = entries["intro"] with
            {
                FloorAlignedContent = existing[0] with
                {
                    DestinationAngles = Array.Empty<double>()
                }
            }
        };
        ExpectIssue(SegmentContentPlanner.Validate(timeline, malformedFloor),
            SegmentContentIssueKind.InvalidContentEntry, "intro",
            "authored floor content must not have malformed angle data");

        var invalidTimeline = timeline with { SampleRate = 0 };
        ExpectIssue(SegmentContentPlanner.Validate(invalidTimeline, entries),
            SegmentContentIssueKind.InvalidTimeline, null,
            "invalid shared audio sample rate is a typed rejection");

        ExpectCreateRejected(() =>
            SegmentContentPlanner.CreateEmpty(timeline.Partitions[0]),
            "an existing authored revision cannot be turned into empty content");
        ExpectCreateRejected(() =>
            SegmentContentPlanner.CreateEmpty(drop with
            {
                Status = SegmentPartitionStatus.Parked,
                OwnerId = null
            }),
            "a parked allocation cannot create new active empty content");
        ExpectCreateRejected(() =>
            SegmentContentPlanner.CreateEmpty(drop with
            {
                EndSampleExclusive = drop.StartSample
            }),
            "zero-length empty segment must be rejected");

        // Adjacent music-time slices with no tiles at all are also valid.
        var completelyEmpty = new SegmentTimeTimeline(SampleRate, []);
        completelyEmpty = SegmentTimePlanner.CreateEmpty(
            completelyEmpty, "verse", Sample(10), Sample(20), "A");
        completelyEmpty = SegmentTimePlanner.CreateEmpty(
            completelyEmpty, "chorus", Sample(20), Sample(40), "B");
        var emptyEntries = completelyEmpty.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty, StringComparer.Ordinal);
        Check(SegmentContentPlanner.Validate(completelyEmpty, emptyEntries).Accepted,
            "the entire draft may consist of empty, tile-free music sections");

        // Parked authored revisions remain retained but need not be published.
        var withParked = timeline with
        {
            Partitions = timeline.Partitions.Concat(
            [
                new SegmentTimePartition(
                    "parked", Sample(40), Sample(80), "archived-r1",
                    Sample(40), null, SegmentPartitionStatus.Parked)
            ]).ToArray()
        };
        var withParkedEntries = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal)
        {
            ["parked"] = new("parked", SegmentContentKind.FloorAligned,
                "archived-r1", existing[1])
        };
        Check(SegmentContentPlanner.Validate(withParked, withParkedEntries).Accepted,
            "a parked revision can coexist with another author's empty active section");

        Check(timelineBefore == JsonSerializer.Serialize(timeline),
            "validation must never modify time ownership");
        Check(entriesBefore == JsonSerializer.Serialize(entries),
            "validation must never modify the caller's content dictionary");
        Check(originalIntro == JsonSerializer.Serialize(existing[0]) &&
              originalOutro == JsonSerializer.Serialize(existing[1]),
            "validation must not rewrite any authored Segment payload");
        Check(chartBefore == JsonSerializer.Serialize(new
        {
            originalChart.Angles,
            Actions = originalChart.ActionStore.Actions,
            originalChart.Decorations,
            originalChart.Positions
        }), "source chart remains byte-equivalent by modeled fields");
    }

    private static void ExpectCreateRejected(
        Action create, string description)
    {
        try
        {
            create();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Unsafe empty Segment creation accepted: " + description);
    }

    private static void ExpectIssue(
        SegmentContentValidationResult result,
        SegmentContentIssueKind kind,
        string? segmentId, string message)
    {
        Check(!result.Accepted &&
              result.Issues.Any(issue =>
                  issue.Kind == kind && issue.SegmentId == segmentId),
            message + ": expected " + kind);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
