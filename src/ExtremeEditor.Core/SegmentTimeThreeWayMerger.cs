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
/// RED-only semantic three-way merge contract for segment time allocations.
/// Inputs are validated, independent snapshots from common ancestor / ours /
/// theirs. Merge decisions are by stable SegmentId, not array position.
/// </summary>
public static class SegmentTimeThreeWayMerger
{
    public static SegmentTimeMergeResult Merge(
        SegmentTimeTimeline commonAncestor,
        SegmentTimeTimeline ours,
        SegmentTimeTimeline theirs) =>
        throw new NotImplementedException("RED: semantic 3-way merge for audio-sample segment allocations");
}
