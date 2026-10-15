namespace ExtremeEditor.Core;

/// <summary>
/// An .adofai output verified by the Core loader and associated with a
/// precise, published collaboration commit. Does not guarantee stock-game
/// playability or parity with advanced event semantics.
/// </summary>
public sealed record SegmentGitAdoFaiPublication(
    string SourceCommit,
    string DestinationPath,
    int FloorCount,
    int ActionCount);

/// <summary>
/// Publishes the committed and fully synchronized main snapshot from an
/// isolated SegmentGitWorkspace. No uncommitted drafts or unpublished
/// collaborator revisions are eligible. This bridge itself never Pushes,
/// Pulls, moves HEAD or mutates workspace-owned files.
/// </summary>
public static class SegmentGitAdoFaiPipeline
{
    public static SegmentGitAdoFaiPublication ExportPublishedHead(
        LevelDocument sourceTemplate,
        SegmentGitWorkspace workspace,
        string destinationPath,
        bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!string.Equals(workspace.CurrentBranch, "main", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Only the synchronized main collaboration branch can be exported.");

        // Validate the collaboration-owned worktree rather than blindly
        // calling Load(): a draft is not necessarily the committed snapshot.
        // Unrelated, untracked user files are intentionally left alone.
        workspace.AssertCleanOwnedWorktree();

        string committed = ResolveMainCommit(workspace);
        string published = SegmentGitRemoteSync.Fetch(workspace, "main");
        if (!string.Equals(committed, published, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Local collaboration main differs from published origin/main. " +
                "Resolve or publish all author edits before exporting.");

        // Fetch only advances the tracking ref. Detect a local concurrent
        // editor mutation before trusting the snapshot to represent HEAD.
        workspace.AssertCleanOwnedWorktree();
        EnsureUnchangedHead(workspace, committed);

        // Read from the immutable SHA, not from a potentially stale in-memory
        // or modified worktree snapshot. Parked revisions remain in this
        // immutable snapshot, but the composer excludes them from the chart.
        SegmentGitSnapshot snapshot = workspace.ReadAt(committed);
        SegmentEditedCompositionResult composed =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                sourceTemplate, snapshot.Timeline, snapshot.Segments);
        if (!composed.Accepted || composed.Document is null ||
            composed.Timing is null)
        {
            string problems = string.Join("; ", composed.Issues.Select(
                item => $"{item.Kind} [{item.SegmentId ?? "<global>"}] " +
                        $"floor {item.Floor}: {item.Detail}"));
            throw new InvalidOperationException(
                "The published collaboration snapshot is not exportable: " + problems);
        }

        workspace.AssertCleanOwnedWorktree();
        EnsureUnchangedHead(workspace, committed);

        // Reuses the guarded export service, which validates AGAIN,
        // writes a unique temporary .adofai, reopens it with the production
        // loader and only then publishes the destination. The original raw
        // source JSON remains external to the collaboration repository.
        SegmentAdoFaiExportResult exported =
            SegmentAdoFaiExporter.ExportValidated(
                sourceTemplate, snapshot.Timeline, snapshot.Segments,
                destinationPath, overwriteExisting);

        return new SegmentGitAdoFaiPublication(
            committed, exported.DestinationPath,
            exported.FloorCount, exported.ActionCount);
    }

    private static void EnsureUnchangedHead(
        SegmentGitWorkspace workspace, string expected)
    {
        if (!string.Equals(workspace.CurrentBranch, "main", StringComparison.Ordinal) ||
            !string.Equals(ResolveMainCommit(workspace), expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Collaboration main advanced during export preparation.");
    }

    private static string ResolveMainCommit(SegmentGitWorkspace workspace)
    {
        string sha = workspace.ExecuteGit(
            "rev-parse", "--verify", "--end-of-options",
            "refs/heads/main^{commit}").Trim();
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException(
                "Collaboration HEAD did not resolve to a valid commit SHA.");
        return sha;
    }
}
