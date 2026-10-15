namespace ExtremeEditor.Core;

/// <summary>
/// Result of merging one existing local collaboration Git branch into the
/// currently checked-out collaboration branch. A conflicting merge never
/// returns a new commit or changes the current HEAD/index/working tree.
/// </summary>
public sealed record SegmentGitMergeResult(
    string BaseCommit,
    string? MergeCommit,
    IReadOnlyList<SegmentTimeMergeConflict> TimelineConflicts,
    IReadOnlyList<string> ContentConflictSegmentIds)
{
    public bool Success =>
        MergeCommit is not null &&
        TimelineConflicts.Count == 0 &&
        ContentConflictSegmentIds.Count == 0;
}

/// <summary>
/// RED-only bridge between Git history, independently versioned LevelSegments,
/// and the existing semantic timeline 3-way merger.
/// This is a local-only operation on a dedicated SegmentGitWorkspace.
/// It must discover the Git merge-base itself, not trust a caller-supplied base.
/// </summary>
public static class SegmentGitBranchMerger
{
    public static SegmentGitMergeResult MergeIntoCurrent(
        SegmentGitWorkspace workspace,
        string sourceBranch) =>
        throw new NotImplementedException(
            "RED: resolve Git merge-base, reconcile Segment payloads and timeline, " +
            "then create a safe two-parent local merge commit");
}
