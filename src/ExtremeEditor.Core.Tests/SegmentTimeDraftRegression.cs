using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// RED: an author should be able to start placing timed tile markers
/// in an otherwise completely empty musical section. No floor synthesis,
/// stock-ADOFAI playback, save/export, GUI operation or Git side effects.
/// </summary>
internal static class SegmentTimeDraftRegression
{
    private const int Rate = 48_000;
    private static long S(long seconds) => checked(seconds * Rate);

    public static void Run()
    {
        var timeline = new SegmentTimeTimeline(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "intro", S(0), S(40), "A");
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "drop", S(40), S(80), "B");
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "outro", S(80), S(120), "C");

        var entries = timeline.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty,
            StringComparer.Ordinal);
        Check(SegmentContentPlanner.Validate(timeline, entries).Accepted,
            "fixture: empty draft sections must be valid");

        string baselineTimeline = JsonSerializer.Serialize(timeline);
        string baselineEntries = JsonSerializer.Serialize(entries);

        // Open a truly empty music-time interval, independently of any
        // previous floor, invented entry angle, or owner A/C's content.
        SegmentTimeDraft initial = SegmentTimeDraftEditor.Open(
            timeline, entries, "drop", "B");
        Equal("drop", initial.SegmentId, "identity retained");
        Equal("B", initial.OwnerId, "editor owns this region");
        Equal(Rate, initial.SampleRate, "common decoded audio sample rate");
        Equal(S(40), initial.StartSample, "left endpoint inclusive");
        Equal(S(80), initial.EndSampleExclusive, "right endpoint exclusive");
        Check(initial.IsEmpty && initial.TileAnchors.Count == 0,
            "no implicit tile, floor, geometry or dummy entry anchor");

        // Insertion order and chronological order differ. The new snapshot
        // should not rewrite previously returned drafts or source allocations.
        SegmentTimeDraft one = SegmentTimeDraftEditor.PlaceAnchor(
            initial, "middle", S(60));
        SegmentTimeDraft two = SegmentTimeDraftEditor.PlaceAnchor(
            one, "start", S(40));
        SegmentTimeDraft three = SegmentTimeDraftEditor.PlaceAnchor(
            two, "later", S(79));
        SegmentTimeDraft four = SegmentTimeDraftEditor.PlaceAnchor(
            three, "same-time", S(60));

        Check(four.TileAnchors.Select(t => t.TileId).SequenceEqual(
            ["start", "middle", "same-time", "later"]),
            "anchors sorted by audio sample with stable order for equal-time tiles");
        Check(four.TileAnchors.Select(t => t.AudioSample).SequenceEqual(
            [S(40), S(60), S(60), S(79)]),
            "absolute song-sample positions must not be retimed");
        Check(initial.IsEmpty && initial.TileAnchors.Count == 0,
            "placing tiles cannot mutate an old empty draft");
        Check(one.TileAnchors.Select(t => t.TileId).SequenceEqual(["middle"]),
            "previous placement snapshot remains unchanged");

        SegmentTimeDraft removed = SegmentTimeDraftEditor.RemoveAnchor(
            four, "middle");
        Check(removed.TileAnchors.Select(t => t.TileId).SequenceEqual(
            ["start", "same-time", "later"]),
            "removing one equal-time tile preserves the other");
        Check(four.TileAnchors.Count == 4,
            "removal does not mutate older immutable draft snapshot");

        SegmentTimeDraft emptyAgain = removed;
        foreach (string id in new[] { "start", "same-time", "later" })
            emptyAgain = SegmentTimeDraftEditor.RemoveAnchor(emptyAgain, id);
        Check(emptyAgain.IsEmpty && emptyAgain.TileAnchors.Count == 0,
            "deleting all tile anchors leaves the allocated Segment intact");
        Equal(S(40), emptyAgain.StartSample, "deletion preserves ownership start");
        Equal(S(80), emptyAgain.EndSampleExclusive, "deletion preserves ownership end");

        // The interval is [40s,80s), not [40s,80s].
        ExpectRejected(() => SegmentTimeDraftEditor.PlaceAnchor(initial, "at-end", S(80)),
            "end sample belongs to the next Segment");
        ExpectRejected(() => SegmentTimeDraftEditor.PlaceAnchor(initial, "before", S(39)),
            "cannot place in previous owner's interval");
        ExpectRejected(() => SegmentTimeDraftEditor.PlaceAnchor(initial, "after", S(100)),
            "cannot place in later owner's interval");
        ExpectRejected(() => SegmentTimeDraftEditor.PlaceAnchor(one, "middle", S(61)),
            "duplicate IDs are not legal even if the timestamp is different");
        ExpectRejected(() => SegmentTimeDraftEditor.PlaceAnchor(initial, "", S(50)),
            "tile ID must be nonempty");
        ExpectRejected(() => SegmentTimeDraftEditor.RemoveAnchor(initial, "missing"),
            "removing an unknown marker must be rejected");

        ExpectRejected(() => SegmentTimeDraftEditor.Open(
            timeline, entries, "drop", "A"),
            "another author cannot open B's time interval for editing");
        ExpectRejected(() => SegmentTimeDraftEditor.Open(
            timeline, entries, "unknown", "B"),
            "unknown Segment cannot be opened");

        var missingContent = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal);
        missingContent.Remove("drop");
        ExpectRejected(() => SegmentTimeDraftEditor.Open(
            timeline, missingContent, "drop", "B"),
            "missing content is not a new empty Segment");

        var invalidContent = new Dictionary<string, SegmentContentEntry>(
            entries, StringComparer.Ordinal);
        invalidContent["drop"] = entries["drop"] with
        {
            RevisionId = "author-made-data"
        };
        ExpectRejected(() => SegmentTimeDraftEditor.Open(
            timeline, invalidContent, "drop", "B"),
            "a revision mismatch must not be interpreted as empty");

        var parked = timeline with
        {
            Partitions = timeline.Partitions.Select(p => p.SegmentId == "drop"
                ? p with { Status = SegmentPartitionStatus.Parked, OwnerId = null }
                : p).ToArray()
        };
        ExpectRejected(() => SegmentTimeDraftEditor.Open(
            parked, entries, "drop", "B"),
            "parked content cannot be edited as active");

        Check(baselineTimeline == JsonSerializer.Serialize(timeline),
            "draft operations never change the source ownership timeline");
        Check(baselineEntries == JsonSerializer.Serialize(entries),
            "draft operations never rewrite the source content entries");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                message + ": expected " + expected + ", actual " + actual);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectRejected(Action operation, string message)
    {
        try
        {
            operation();
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
            "Unsafe time-first draft operation accepted: " + message);
    }
}
