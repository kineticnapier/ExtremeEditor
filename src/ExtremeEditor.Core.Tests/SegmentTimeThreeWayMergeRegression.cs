using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class SegmentTimeThreeWayMergeRegression
{
    private const int SampleRate = 48_000;
    private static long S(long seconds) => checked(seconds * SampleRate);

    public static void Run()
    {
        // All three inputs are complete, independently valid snapshots of the
        // same project. The stable IDs, not the order of Partitions, identify
        // which segment changed since their common ancestor.
        SegmentTimeTimeline ancestor = Base();
        SegmentTimePartition[] untouched = ancestor.Partitions.ToArray();

        SegmentTimeTimeline ours = Copy(ancestor,
            Segment(ancestor, "B") with { ContentRevisionId = "B-r2" },
            NewActive("D", 140, 150, "A"));
        SegmentTimeTimeline theirs = Copy(ancestor,
            Segment(ancestor, "C") with { ContentRevisionId = "C-r2" },
            NewActive("E", 150, 160, "B"));

        // Deliberately reorder independently saved entries. Array ordering
        // must not change merge semantics or create a false conflict.
        theirs = theirs with { Partitions = theirs.Partitions.Reverse().ToArray() };
        SegmentTimeMergeResult merged = SegmentTimeThreeWayMerger.Merge(ancestor, ours, theirs);
        Ok(merged, "independent edits and additions");
        SegmentTimeTimeline combined = merged.Timeline!;
        SegmentTimePlanner.Validate(combined);
        Equal(6, combined.Partitions.Count, "all unchanged and new IDs preserved");
        Equal("B-r2", Segment(combined, "B").ContentRevisionId, "ours edits B");
        Equal("C-r2", Segment(combined, "C").ContentRevisionId, "theirs edits C");
        Equal(S(140), Segment(combined, "D").StartSample, "ours new D");
        Equal(S(150), Segment(combined, "E").StartSample, "theirs new E");
        Equal("park-rev", Segment(combined, "P").ContentRevisionId, "unrelated parked content retained");
        Equal(S(50), Segment(combined, "B").ContentOriginSample,
            "content's audio origin does not move in a merge");

        Sequence(untouched, ancestor.Partitions, "ancestor is untouched");
        Equal("B-r2", Segment(ours, "B").ContentRevisionId, "ours remains independent");
        Equal("B-r1", Segment(theirs, "B").ContentRevisionId, "theirs remains independent");

        // When both branches made the same change, merge is idempotent.
        SegmentTimeTimeline identical = Copy(ancestor,
            Segment(ancestor, "A") with { ContentRevisionId = "A-r2" });
        SegmentTimeMergeResult converged = SegmentTimeThreeWayMerger.Merge(
            ancestor, identical, identical);
        Ok(converged, "same independent edit must converge");
        Equal("A-r2", Segment(converged.Timeline!, "A").ContentRevisionId, "convergent content");

        // Same ID but independent changes: don't pick a favorite, even if
        // the changes happen to affect different record fields.
        SegmentTimeTimeline editB = Copy(ancestor,
            Segment(ancestor, "B") with { ContentRevisionId = "B-r2" });
        SegmentTimeTimeline reassignB = Copy(ancestor,
            Segment(ancestor, "B") with { OwnerId = "C" });
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, editB, reassignB),
            SegmentTimeMergeConflictKind.DivergentEdit, "B", "same identity modified twice");

        SegmentTimeTimeline editBAlternative = Copy(ancestor,
            Segment(ancestor, "B") with { ContentRevisionId = "B-r3" });
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, editB, editBAlternative),
            SegmentTimeMergeConflictKind.DivergentEdit, "B", "divergent content revisions");

        // Deleted on one branch, modified on the other must not lose work.
        SegmentTimeTimeline deletedB = Without(ancestor, "B");
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, deletedB, editB),
            SegmentTimeMergeConflictKind.DeleteModify, "B", "delete vs edit");
        // If the other side did not edit the deleted ID, deletion is safe.
        SegmentTimeMergeResult safeDelete = SegmentTimeThreeWayMerger.Merge(
            ancestor, deletedB, ancestor);
        Ok(safeDelete, "deletion vs unchanged ancestor");
        Check(safeDelete.Timeline!.Partitions.All(p => p.SegmentId != "B"),
            "unchanged side must not resurrect deleted segment");

        // Both sides independently create the same stable ID differently.
        SegmentTimeTimeline newLeft = Copy(ancestor, NewActive("X", 140, 150, "A"));
        SegmentTimeTimeline newRight = Copy(ancestor, NewActive("X", 150, 160, "B"));
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, newLeft, newRight),
            SegmentTimeMergeConflictKind.ConcurrentCreation, "X",
            "same new ID created differently");

        // Git's file-level merge might accept both of these new, different
        // Segment IDs; project semantics must detect the resulting overlap.
        SegmentTimeTimeline overlapLeft = Copy(ancestor, NewActive("L", 140, 160, "A"));
        SegmentTimeTimeline overlapRight = Copy(ancestor, NewActive("R", 150, 170, "B"));
        SegmentTimePlanner.Validate(overlapLeft);
        SegmentTimePlanner.Validate(overlapRight);
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, overlapLeft, overlapRight),
            SegmentTimeMergeConflictKind.ActiveRangeOverlap, "L",
            "independently valid assignments overlap after merge", "R");

        // Parked revisions never reserve their previous audio range. A
        // different branch may allocate that range without erasing the archive.
        SegmentTimeTimeline parkBase = new(SampleRate,
        [
            new("old", S(80), S(100), "old-r1", S(50), null, SegmentPartitionStatus.Parked)
        ]);
        SegmentTimeTimeline parkOurs = Copy(parkBase, NewActive("new", 80, 100, "C"));
        SegmentTimeTimeline parkTheirs = Copy(parkBase,
            Segment(parkBase, "old") with { ContentRevisionId = "old-r2" });
        SegmentTimeMergeResult parked = SegmentTimeThreeWayMerger.Merge(
            parkBase, parkOurs, parkTheirs);
        Ok(parked, "parked revision and independently allocated active segment");
        Equal("old-r2", Segment(parked.Timeline!, "old").ContentRevisionId,
            "parked history revision preserved");
        Equal(S(50), Segment(parked.Timeline!, "old").ContentOriginSample,
            "parked content origin remains anchored");
        Equal(S(80), Segment(parked.Timeline!, "new").StartSample,
            "the reused range remains at its original musical time");

        // A project has ONE decoded-audio sample-rate coordinate system.
        // Otherwise matching long sample positions refer to different times.
        SegmentTimeTimeline wrongRate = ancestor with { SampleRate = 44_100 };
        Conflict(SegmentTimeThreeWayMerger.Merge(ancestor, ancestor, wrongRate),
            SegmentTimeMergeConflictKind.SampleRateMismatch, "", "sample-rate mismatch");

        // Reject an invalid input snapshot before attempting reconciliation.
        var invalid = ancestor with
        {
            Partitions =
            [
                ..ancestor.Partitions,
                NewActive("invalid-overlap", 35, 45, "B")
            ]
        };
        ExpectInvalid(() => SegmentTimeThreeWayMerger.Merge(ancestor, invalid, ancestor),
            "overlapping source snapshot");

        // This RED proves allocation metadata only, not actual playable .adofai
        // content merge, Git network operations, or segment boundary timing.
    }

    private static SegmentTimeTimeline Base() => new(SampleRate,
    [
        NewActive("A", 0, 40, "A") with { ContentRevisionId = "A-r1" },
        NewActive("B", 50, 100, "B") with { ContentRevisionId = "B-r1" },
        NewActive("C", 100, 140, "C") with { ContentRevisionId = "C-r1" },
        new("P", S(80), S(100), "park-rev", S(50), null, SegmentPartitionStatus.Parked)
    ]);

    private static SegmentTimePartition NewActive(string id, long start, long end, string owner) =>
        new(id, S(start), S(end), null, S(start), owner, SegmentPartitionStatus.Active);

    private static SegmentTimeTimeline Copy(
        SegmentTimeTimeline timeline, params SegmentTimePartition[] changes)
    {
        var byId = timeline.Partitions.ToDictionary(p => p.SegmentId, StringComparer.Ordinal);
        foreach (SegmentTimePartition item in changes)
            byId[item.SegmentId] = item;
        return new SegmentTimeTimeline(timeline.SampleRate, byId.Values.ToArray());
    }

    private static SegmentTimeTimeline Without(SegmentTimeTimeline timeline, string id) =>
        timeline with { Partitions = timeline.Partitions.Where(p => p.SegmentId != id).ToArray() };

    private static SegmentTimePartition Segment(SegmentTimeTimeline timeline, string id) =>
        timeline.Partitions.Single(p => p.SegmentId == id);

    private static void Ok(SegmentTimeMergeResult result, string description)
    {
        if (!result.Success || result.Timeline is null || result.Conflicts.Count != 0)
            throw new InvalidOperationException($"Expected valid three-way merge: {description}");
    }

    private static void Conflict(
        SegmentTimeMergeResult result, SegmentTimeMergeConflictKind kind,
        string id, string description, string? otherId = null)
    {
        if (result.Success || result.Timeline is not null)
            throw new InvalidOperationException($"Conflicted merge was incorrectly accepted: {description}");
        if (!result.Conflicts.Any(c => c.Kind == kind &&
                                      c.SegmentId == id && c.OtherSegmentId == otherId))
            throw new InvalidOperationException(
                $"Missing {kind} conflict for segment {id}/{otherId}: {description}");
    }

    private static void Check(bool valid, string description)
    {
        if (!valid)
            throw new InvalidOperationException(description);
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

    private static void ExpectInvalid(Action action, string description)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException($"Invalid input accepted: {description}");
    }
}
