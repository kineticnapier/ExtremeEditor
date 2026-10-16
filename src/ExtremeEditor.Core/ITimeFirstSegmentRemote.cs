namespace ExtremeEditor.Core;

/// <summary>
/// Supplies Git commits for time-first Together synchronization.
/// Fetch must import all required commit/tree/blob objects into the local repository
/// and update refs/remotes/origin/{branchName} to the returned commit.
/// Push must reject non-fast-forward updates and must never discard local commits.
/// A remote implementation must validate all untrusted data before publication.
/// </summary>
public interface ITimeFirstSegmentRemote
{
    string Fetch(TimeFirstSegmentGitWorkspace workspace, string branchName);
    string Push(TimeFirstSegmentGitWorkspace workspace, string branchName);
}

/// <summary>
/// Compatibility adapter for the existing trusted local bare Git repository.
/// It does not authorize network Git URLs or bypass local origin validation.
/// </summary>
public sealed class LocalBareTimeFirstSegmentRemote : ITimeFirstSegmentRemote
{
    public static LocalBareTimeFirstSegmentRemote Instance { get; } = new();

    private LocalBareTimeFirstSegmentRemote() { }

    public string Fetch(TimeFirstSegmentGitWorkspace workspace, string branchName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return SegmentGitRemoteSync.Fetch(workspace.Transport, branchName);
    }

    public string Push(TimeFirstSegmentGitWorkspace workspace, string branchName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return SegmentGitRemoteSync.Push(workspace.Transport, branchName);
    }
}
