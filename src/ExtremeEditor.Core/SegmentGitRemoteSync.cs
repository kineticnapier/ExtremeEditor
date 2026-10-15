namespace ExtremeEditor.Core;

/// <summary>
/// Result of fetching and semantically incorporating a remote branch into
/// the checked-out local branch. On conflict, LocalCommit is null and no
/// local branch or worktree changes are permitted.
/// </summary>
public sealed record SegmentGitPullResult(
    string RemoteCommit,
    string? LocalCommit,
    IReadOnlyList<SegmentTimeMergeConflict> TimelineConflicts,
    IReadOnlyList<string> ContentConflictSegmentIds)
{
    public bool Success =>
        LocalCommit is not null &&
        TimelineConflicts.Count == 0 &&
        ContentConflictSegmentIds.Count == 0;
}

/// <summary>
/// RED-only local-bare-remote transport contract for ExtremeEditor Together.
/// A remote is an explicitly selected local bare Git repository; no web,
/// credentials, external protocols or GitHub interactions are involved.
/// </summary>
public static class SegmentGitRemoteSync
{
    /// <summary>
    /// Configure one vetted local bare repo as the workspace's origin.
    /// Never change the ExtremeEditor source checkout or global Git config.
    /// </summary>
    public static void ConnectLocalBare(
        SegmentGitWorkspace workspace, string bareRepositoryPath) =>
        throw new NotImplementedException("RED: attach local bare Git remote safely");

    /// <summary>
    /// Clone an already published collaboration workspace into a new or
    /// empty directory, verifying its schema/marker before accepting it.
    /// </summary>
    public static SegmentGitWorkspace CloneLocalBare(
        string bareRepositoryPath, string destinationDirectory) =>
        throw new NotImplementedException("RED: clone independent collaboration client");

    /// <summary>
    /// Push only the explicitly named local branch, with regular non-force
    /// fast-forward protection. Never push unrelated local branch refs.
    /// Returns the remote branch's new full commit SHA.
    /// </summary>
    public static string Push(
        SegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException("RED: safe non-force branch push");

    /// <summary>
    /// Update remote-tracking refs without changing local HEAD, the index,
    /// working tree, or uncommitted content. Return the fetched full SHA.
    /// </summary>
    public static string Fetch(
        SegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException("RED: safe fetch of one branch");

    /// <summary>
    /// Fetch and incorporate the remote-tracking revision into the current
    /// local branch. Allow fast-forward or semantic two-parent merge. Return
    /// typed semantic conflicts without changing HEAD/worktree on conflict.
    /// Never rely on git pull's textual automatic merge.
    /// </summary>
    public static SegmentGitPullResult Pull(
        SegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException("RED: fetch plus semantic pull");
}
