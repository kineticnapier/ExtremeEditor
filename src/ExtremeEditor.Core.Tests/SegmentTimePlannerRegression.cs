using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class SegmentTimePlannerRegression
{
    private const int SampleRate = 48_000;
    private static long S(long seconds) => checked(seconds * SampleRate);

    public static void Run()
    {
        // This test validates timeline metadata against an actual LevelSegment
        // produced by the previous PoC, not a playable arbitrary-time cut.
        LevelDocument chart = LevelDocument.CreateSynthetic(7);
        LevelSegment[] segments = LevelSegmentPartitioner.Split(chart, 2, 5).ToArray();
        double[] originalAngles = chart.Angles.ToArray();
        double[] originalSegmentAngles = segments[1].DestinationAngles.ToArray();

        var before = new SegmentTimeTimeline(SampleRate,
        [
            new("seg-A", S(0), S(40), "revision-A1", S(0), "A", SegmentPartitionStatus.Active),
            new("seg-B", S(50), S(100), "revision-B1", S(50), "B", SegmentPartitionStatus.Active),
            new("seg-C", S(100), S(140), "revision-C1", S(100), "C", SegmentPartitionStatus.Active)
        ]);

        SegmentTimePlanner.Validate(before); // RED: must reject invalid ranges but allow intentional gaps.
        var unchanged = before.Partitions.ToArray();

        // Reshape B's [50,100) assignment into [40,80):
        //   [40,50) freshly created B segment;
        //   [50,80) original B content at the same audio timestamp;
        //   [80,100) old content kept but parked, NOT silently deleted.
        SegmentTimeTimeline split = SegmentTimePlanner.SplitAt(
            before, "seg-B", S(80), "seg-B-excess");
        SegmentTimePlanner.Validate(split);
        Check(split.SampleRate == SampleRate, "shared sample rate retained");
        Check(split.Partitions.Count == 4, "split creates one additional partition");
        SegmentTimePartition retained = Find(split, "seg-B");
        SegmentTimePartition excess = Find(split, "seg-B-excess");
        Range(retained, 50, 80, SegmentPartitionStatus.Active);
        Range(excess, 80, 100, SegmentPartitionStatus.Active);
        Equal(retained.ContentRevisionId, excess.ContentRevisionId,
            "a split shares the immutable original content revision");
        Equal("revision-B1", excess.ContentRevisionId,
            "content reference remains unchanged");
        Equal(S(50), excess.ContentOriginSample,
            "splitting must not reset content time origin to 80 seconds");
        Equal(S(30), excess.StartSample - excess.ContentOriginSample,
            "the excess portion begins 30 seconds into the original content");

        SegmentTimeTimeline parked = SegmentTimePlanner.Park(split, "seg-B-excess");
        Range(Find(parked, "seg-B-excess"), 80, 100, SegmentPartitionStatus.Parked);
        Equal("revision-B1", Find(parked, "seg-B-excess").ContentRevisionId,
            "parking preserves revision for later recovery");
        Check(Find(parked, "seg-B-excess").OwnerId is null,
            "parked range must no longer be assigned");

        SegmentTimeTimeline reassigned = SegmentTimePlanner.CreateEmpty(
            parked, "seg-B-before", S(40), S(50), "B");
        SegmentTimePlanner.Validate(reassigned);
        Range(Find(reassigned, "seg-B-before"), 40, 50, SegmentPartitionStatus.Active);
        Check(Find(reassigned, "seg-B-before").ContentRevisionId is null,
            "new segment has independent empty content, not a moved or copied old chart");
        Equal(S(40), Find(reassigned, "seg-B-before").ContentOriginSample,
            "new segment's initial content time origin");
        Equal("B", Find(reassigned, "seg-B-before").OwnerId,
            "new segment assigned to B");

        var b = reassigned.Partitions
            .Where(part => part.Status == SegmentPartitionStatus.Active && part.OwnerId == "B")
            .OrderBy(part => part.StartSample)
            .ToArray();
        Equal(2, b.Length, "B has exactly the new and retained subsegments");
        Range(b[0], 40, 50, SegmentPartitionStatus.Active);
        Range(b[1], 50, 80, SegmentPartitionStatus.Active);

        // Boundary is half-open. Unassigned [80,100) is now independently
        // editable by another person while the parked revision stays intact.
        SegmentTimeTimeline reused = SegmentTimePlanner.CreateEmpty(
            reassigned, "seg-C-new", S(80), S(100), "C");
        SegmentTimePlanner.Validate(reused);
        Equal("revision-B1", Find(reused, "seg-B-excess").ContentRevisionId,
            "old [80,100) is still available for recovery");
        Equal("revision-C1", Find(reused, "seg-C").ContentRevisionId,
            "C's old segment was not modified");

        // The original timeline and actual LevelDocument content must not
        // be modified by metadata operations or implicit Git-style merging.
        Equal(3, before.Partitions.Count, "source timeline partition count");
        Sequence(unchanged, before.Partitions, "source partitions are unchanged");
        Sequence(originalAngles, chart.Angles, "chart angles untouched");
        Sequence(originalSegmentAngles, segments[1].DestinationAngles,
            "existing floor-aligned Segment data untouched");

        // Reject dangerous allocation operations, regardless of whether two
        // different content files could be merged successfully by Git.
        ExpectRejected(() => SegmentTimePlanner.CreateEmpty(
                reassigned, "overlap", S(79), S(81), "A"),
            "overlap with active B [50,80)");
        ExpectRejected(() => SegmentTimePlanner.CreateEmpty(
                reassigned, "duplicate", S(40), S(50), "A"),
            "duplicate allocation");
        ExpectRejected(() => SegmentTimePlanner.CreateEmpty(
                reassigned, "seg-B", S(140), S(150), "B"),
            "duplicate segment identity");
        ExpectRejected(() => SegmentTimePlanner.CreateEmpty(
                reassigned, "zero", S(20), S(20), "B"),
            "empty interval");
        ExpectRejected(() => SegmentTimePlanner.CreateEmpty(
                reassigned, "reversed", S(30), S(20), "B"),
            "reversed interval");
        ExpectRejected(() => SegmentTimePlanner.SplitAt(
                before, "seg-B", S(50), "right"),
            "cannot split at start boundary");
        ExpectRejected(() => SegmentTimePlanner.SplitAt(
                before, "seg-B", S(100), "right"),
            "cannot split at end boundary");
        ExpectRejected(() => SegmentTimePlanner.SplitAt(
                before, "missing", S(75), "right"),
            "unknown segment ID");

        ExpectRejected(() => SegmentTimePlanner.Validate(new SegmentTimeTimeline(SampleRate,
        [
            new("one", S(0), S(80), "r1", S(0), "A", SegmentPartitionStatus.Active),
            new("two", S(70), S(90), "r2", S(70), "B", SegmentPartitionStatus.Active)
        ])), "two different Segment IDs overlap in audio time");

        ExpectRejected(() => SegmentTimePlanner.Validate(new SegmentTimeTimeline(SampleRate,
        [
            new("dup", S(0), S(20), "r1", S(0), "A", SegmentPartitionStatus.Active),
            new("dup", S(25), S(40), "r2", S(25), "B", SegmentPartitionStatus.Active)
        ])), "duplicate IDs despite distinct time ranges");

        ExpectRejected(() => SegmentTimePlanner.Validate(new SegmentTimeTimeline(0, [])),
            "missing shared audio sample rate");
    }

    private static SegmentTimePartition Find(SegmentTimeTimeline timeline, string id) =>
        timeline.Partitions.Single(part => part.SegmentId == id);

    private static void Range(
        SegmentTimePartition part, long startSeconds, long endSeconds, SegmentPartitionStatus state)
    {
        Equal(S(startSeconds), part.StartSample, $"start sample of {part.SegmentId}");
        Equal(S(endSeconds), part.EndSampleExclusive, $"end sample of {part.SegmentId}");
        Equal(S(endSeconds - startSeconds), part.LengthSamples, $"length of {part.SegmentId}");
        Equal(state, part.Status, $"state of {part.SegmentId}");
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string description)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Sequence differs: {description}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private static void ExpectRejected(Action action, string description)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"Unsafe timeline operation accepted: {description}");
    }
}
