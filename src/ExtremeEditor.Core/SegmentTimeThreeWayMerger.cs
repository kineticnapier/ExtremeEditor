namespace ExtremeEditor.Core;

/// <summary>
/// A semantic conflict found by comparing three complete segment-time snapshots.
/// It is not a Git text-file conflict, and does not imply an ADOFAI chart error.
/// </summary>
public enum SegmentTimeMergeConflictKind : byte
{
    DivergentEdit,
    DeleteModify,
    ConcurrentCreation,
    ActiveRangeOverlap,
    SampleRateMismatch
}

public sealed record SegmentTimeMergeConflict(
    SegmentTimeMergeConflictKind Kind,
    string SegmentId,
    string? OtherSegmentId = null);

/// <summary>
/// On success, Timeline is the validated merged allocation snapshot.
/// On conflict, Timeline is null: never silently choose an invalid partial merge.
/// </summary>
public sealed record SegmentTimeMergeResult(
    SegmentTimeTimeline? Timeline,
    IReadOnlyList<SegmentTimeMergeConflict> Conflicts)
{
    public bool Success => Timeline is not null && Conflicts.Count == 0;
}

/// <summary>
/// Semantic three-way merge of independent, validated time-allocation snapshots.
/// Decisions are based on stable SegmentId, never collection order. Conflicts
/// are returned without a partial timeline; no chart or Git data is modified.
/// </summary>
public static class SegmentTimeThreeWayMerger
{
    public static SegmentTimeMergeResult Merge(
        SegmentTimeTimeline commonAncestor,
        SegmentTimeTimeline ours,
        SegmentTimeTimeline theirs)
    {
        ArgumentNullException.ThrowIfNull(commonAncestor);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);

        // Input corruption is a programming/data error, not a merge conflict:
        // a valid, independently authored project snapshot is required.
        SegmentTimePlanner.Validate(commonAncestor);
        SegmentTimePlanner.Validate(ours);
        SegmentTimePlanner.Validate(theirs);

        if (commonAncestor.SampleRate != ours.SampleRate ||
            commonAncestor.SampleRate != theirs.SampleRate)
        {
            return Rejected(
            [
                new SegmentTimeMergeConflict(
                    SegmentTimeMergeConflictKind.SampleRateMismatch, string.Empty)
            ]);
        }

        var ancestors = ToIndex(commonAncestor);
        var left = ToIndex(ours);
        var right = ToIndex(theirs);
        var ids = new SortedSet<string>(ancestors.Keys, StringComparer.Ordinal);
        ids.UnionWith(left.Keys);
        ids.UnionWith(right.Keys);

        var resolved = new List<SegmentTimePartition>(ids.Count);
        var conflicts = new List<SegmentTimeMergeConflict>();

        foreach (string id in ids)
        {
            ancestors.TryGetValue(id, out SegmentTimePartition? original);
            left.TryGetValue(id, out SegmentTimePartition? oursPart);
            right.TryGetValue(id, out SegmentTimePartition? theirsPart);

            if (original is null)
            {
                // New identity: only one branch added it, or both added the
                // same immutable value. Different same-ID creations conflict.
                if (oursPart is not null && theirsPart is not null &&
                    oursPart != theirsPart)
                {
                    conflicts.Add(new(
                        SegmentTimeMergeConflictKind.ConcurrentCreation, id));
                    continue;
                }

                SegmentTimePartition? added = oursPart ?? theirsPart;
                if (added is not null)
                    resolved.Add(added);
                continue;
            }

            if (oursPart is null && theirsPart is null)
                continue; // both independently deleted

            if (oursPart is null)
            {
                if (theirsPart != original)
                    conflicts.Add(new(
                        SegmentTimeMergeConflictKind.DeleteModify, id));
                // Deleted here, unchanged there: deletion wins.
                continue;
            }

            if (theirsPart is null)
            {
                if (oursPart != original)
                    conflicts.Add(new(
                        SegmentTimeMergeConflictKind.DeleteModify, id));
                continue;
            }

            if (oursPart == theirsPart)
            {
                resolved.Add(oursPart); // identical changes, or both unchanged
            }
            else if (oursPart == original)
            {
                resolved.Add(theirsPart); // only theirs changed
            }
            else if (theirsPart == original)
            {
                resolved.Add(oursPart); // only ours changed
            }
            else
            {
                // Field-level combination is unsafe until project-specific
                // semantic rules are available. Preserve both via a conflict.
                conflicts.Add(new(
                    SegmentTimeMergeConflictKind.DivergentEdit, id));
            }
        }

        if (conflicts.Count != 0)
            return Rejected(conflicts);

        // Git merges separate files independently. A valid result still
        // requires a second, semantic check across ALL active intervals.
        // Parked revisions are archives and do not reserve time.
        SegmentTimePartition? widestActive = null;
        foreach (SegmentTimePartition partition in resolved
                     .Where(static part => part.Status == SegmentPartitionStatus.Active)
                     .OrderBy(static part => part.StartSample)
                     .ThenBy(static part => part.EndSampleExclusive)
                     .ThenBy(static part => part.SegmentId, StringComparer.Ordinal))
        {
            if (widestActive is not null &&
                partition.StartSample < widestActive.EndSampleExclusive)
            {
                conflicts.Add(new(
                    SegmentTimeMergeConflictKind.ActiveRangeOverlap,
                    widestActive.SegmentId, partition.SegmentId));
            }

            // Track the furthest end rather than just the previous interval:
            // nested intervals must not hide later overlaps.
            if (widestActive is null ||
                partition.EndSampleExclusive > widestActive.EndSampleExclusive)
                widestActive = partition;
        }

        if (conflicts.Count != 0)
            return Rejected(conflicts);

        SegmentTimePartition[] ordered = resolved
            .OrderBy(static part => part.StartSample)
            .ThenBy(static part => part.SegmentId, StringComparer.Ordinal)
            .ToArray();
        var merged = new SegmentTimeTimeline(
            commonAncestor.SampleRate, ordered);

        // Defense in depth: the algorithm must never return an invalid
        // allocation even if a future validation rule is introduced.
        SegmentTimePlanner.Validate(merged);
        return new SegmentTimeMergeResult(
            merged, Array.Empty<SegmentTimeMergeConflict>());
    }

    private static Dictionary<string, SegmentTimePartition> ToIndex(
        SegmentTimeTimeline timeline) =>
        timeline.Partitions.ToDictionary(
            static partition => partition.SegmentId, StringComparer.Ordinal);

    private static SegmentTimeMergeResult Rejected(
        IReadOnlyList<SegmentTimeMergeConflict> conflicts) =>
        new(null, conflicts.ToArray());
}
