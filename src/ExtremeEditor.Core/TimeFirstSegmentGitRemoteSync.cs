namespace ExtremeEditor.Core;

public sealed record TimeFirstSegmentPullResult(
    string RemoteCommit,
    string? LocalCommit,
    IReadOnlyList<SegmentTimeMergeConflict> TimelineConflicts,
    IReadOnlyList<string> ContentConflictSegmentIds)
{
    public bool Success => LocalCommit is not null &&
        TimelineConflicts.Count == 0 && ContentConflictSegmentIds.Count == 0;
}

public static class TimeFirstSegmentGitRemoteSync
{
    public static void ConnectLocalBare(
        TimeFirstSegmentGitWorkspace workspace, string bareRepositoryPath) =>
        throw new NotImplementedException(
            "RED: connect time-first Workspace to a trusted local bare Git repository.");

    public static TimeFirstSegmentGitWorkspace CloneLocalBare(
        string bareRepositoryPath, string destinationDirectory) =>
        throw new NotImplementedException();

    public static string Push(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException();

    public static string Fetch(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException();

    public static TimeFirstSegmentPullResult Pull(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main") =>
        throw new NotImplementedException();
}
