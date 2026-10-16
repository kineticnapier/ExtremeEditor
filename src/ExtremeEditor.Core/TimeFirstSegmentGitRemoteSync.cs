using System.Text.Json;

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

/// <summary>
/// Local-bare Git transport plus conservative, whole-Segment three-way merge.
/// The time-first JSON manifest is never merged by Git's text merge driver.
/// </summary>
public static class TimeFirstSegmentGitRemoteSync
{
    private const string RemoteName = "origin";
    private const string MarkerName = "time-first-workspace.json";
    private const string ProjectName = "time-first-project.json";
    private const string FolderName = "time-first-segments";

    public static void ConnectLocalBare(
        TimeFirstSegmentGitWorkspace workspace, string bareRepositoryPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitRemoteSync.ConnectLocalBare(workspace.Transport, bareRepositoryPath);
    }

    public static TimeFirstSegmentGitWorkspace CloneLocalBare(
        string bareRepositoryPath, string destinationDirectory)
    {
        _ = SegmentGitRemoteSync.CloneLocalBareTransport(bareRepositoryPath, destinationDirectory);
        TimeFirstSegmentGitWorkspace workspace = TimeFirstSegmentGitWorkspace.Open(destinationDirectory);
        _ = workspace.Load();
        _ = workspace.ReadAt("HEAD");
        return workspace;
    }

    /// <summary>Return the previously fetched canonical commit without network access.</summary>
    public static string CanonicalCommit(TimeFirstSegmentGitWorkspace workspace,
        string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        return ResolveCommit(workspace, "refs/remotes/origin/" + branchName);
    }

    public static string Push(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main",
        ITimeFirstSegmentRemote? remote = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        workspace.AssertCleanOwnedWorktree();
        _ = workspace.ReadAt("HEAD");
        return (remote ?? LocalBareTimeFirstSegmentRemote.Instance).Push(workspace, branchName);
    }

    public static string Fetch(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main",
        ITimeFirstSegmentRemote? remote = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        return (remote ?? LocalBareTimeFirstSegmentRemote.Instance).Fetch(workspace, branchName);
    }

    public static TimeFirstSegmentPullResult Pull(
        TimeFirstSegmentGitWorkspace workspace, string branchName = "main",
        ITimeFirstSegmentRemote? remote = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        if (!string.Equals(workspace.CurrentBranch, branchName, StringComparison.Ordinal))
            throw new InvalidOperationException("Pull must target the checked-out branch.");

        workspace.AssertCleanOwnedWorktree();
        string remoteSha = Fetch(workspace, branchName, remote);
        return MergeFromCommit(workspace, branchName, remoteSha,
            "refs/remotes/origin/" + branchName);
    }

    /// <summary>
    /// Merge a previously fetched participant submission only after checking it
    /// against the caller's trusted baseline and its segment ownership.
    /// The canonical remote-tracking ref is never advanced by this operation.
    /// </summary>
    public static TimeFirstSegmentPullResult MergeSubmission(
        TimeFirstSegmentGitWorkspace workspace, string trustedBaseCommit,
        string memberId, string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 64 ||
            !memberId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid member ID.", nameof(memberId));
        if (workspace.CurrentBranch != branchName)
            throw new InvalidOperationException("Submission merge must target the checked-out branch.");
        workspace.AssertCleanOwnedWorktree();
        string submissionRef = "refs/extremeeditor/submissions/" + memberId + "/" + branchName;
        string proposed = ResolveCommit(workspace, submissionRef);
        // Trust must originate with the caller's previously accepted canonical
        // commit, not a baseline asserted by the participant's bundle.
        TimeFirstSubmissionValidator.Validate(workspace, trustedBaseCommit, proposed, memberId);
        return MergeFromCommit(workspace, branchName, proposed, submissionRef);
    }

    private static TimeFirstSegmentPullResult MergeFromCommit(
        TimeFirstSegmentGitWorkspace workspace, string branchName,
        string remoteSha, string referenceToGuard)
    {
        workspace.AssertCleanOwnedWorktree();
        // Reject invalid remote payloads before modifying any owned data.
        TimeFirstSegmentSnapshot theirs = workspace.ReadAt(remoteSha);
        string oursSha = workspace.HeadCommit;
        TimeFirstSegmentSnapshot ours = workspace.ReadAt(oursSha);
        if (oursSha == remoteSha)
            return Success(remoteSha, oursSha);

        string[] bases = workspace.Transport.ExecuteGit(
            "merge-base", "--all", oursSha, remoteSha)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (bases.Length != 1)
            throw new InvalidOperationException("Pull requires exactly one common Git ancestor.");
        if (bases[0] == remoteSha)
            return Success(remoteSha, oursSha); // already contains remote
        if (bases[0] == oursSha)
        {
            workspace.AssertCleanOwnedWorktree();
            workspace.Transport.ExecuteGit("merge", "--ff-only", "--no-stat", remoteSha);
            return Success(remoteSha, workspace.HeadCommit);
        }

        TimeFirstSegmentSnapshot common = workspace.ReadAt(bases[0]);
        SegmentTimeMergeResult timeline = SegmentTimeThreeWayMerger.Merge(
            common.Timeline, ours.Timeline, theirs.Timeline);
        var conflicts = new SortedSet<string>(StringComparer.Ordinal);
        TimeFirstAudioIdentity? audio = Choose(common.Audio, ours.Audio, theirs.Audio,
            static (a, b) => a == b, "@audio", conflicts);
        IReadOnlyList<MusicTimingPoint>? timing = Choose(
            common.TimingPoints, ours.TimingPoints, theirs.TimingPoints,
            static (a, b) => a is not null && b is not null && a.SequenceEqual(b), "@timing", conflicts);

        var content = new Dictionary<string, SegmentContentEntry>(StringComparer.Ordinal);
        var drafts = new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal);
        var authored = new Dictionary<string, TimeFirstAuthoredSegment>(StringComparer.Ordinal);
        var ids = new SortedSet<string>(common.Content.Keys, StringComparer.Ordinal);
        ids.UnionWith(ours.Content.Keys);
        ids.UnionWith(theirs.Content.Keys);
        foreach (string id in ids)
        {
            SegmentPayload? before = Payload(common, id);
            SegmentPayload? left = Payload(ours, id);
            SegmentPayload? right = Payload(theirs, id);
            SegmentPayload? chosen = Choose(before, left, right, PayloadEquals, id, conflicts);
            if (chosen is null) continue;
            content[id] = chosen.Content;
            drafts[id] = chosen.Draft;
            if (chosen.Authored is not null) authored[id] = chosen.Authored;
        }

        if (!timeline.Success || conflicts.Count != 0)
            return new TimeFirstSegmentPullResult(remoteSha, null,
                timeline.Conflicts.ToArray(), conflicts.ToArray());

        var active = timeline.Timeline!.Partitions
            .Where(static p => p.Status == SegmentPartitionStatus.Active)
            .Select(static p => p.SegmentId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in content.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            content.Remove(id);
            drafts.Remove(id);
            authored.Remove(id);
        }
        var combined = new TimeFirstSegmentSnapshot(audio!, timing!.ToArray(),
            timeline.Timeline, content, drafts, authored);
        try { TimeFirstSegmentGitWorkspace.ValidateSnapshot(combined); }
        catch (ArgumentException ex)
        {
            // Cross-field divergence (for example an edited ownership range
            // versus unchanged authored tiles) must not be silently committed.
            throw new InvalidOperationException(
                "The combined time-first snapshot is inconsistent; no Git files changed.", ex);
        }

        workspace.AssertCleanOwnedWorktree();
        if (workspace.HeadCommit != oursSha ||
            ResolveCommit(workspace, referenceToGuard) != remoteSha)
            throw new InvalidOperationException("Git refs changed during semantic merge.");

        string alternateIndex = Path.Combine(workspace.Directory, ".git",
            "extremeeditor-merge-index-" + Guid.NewGuid().ToString("N"));
        bool wrote = false;
        bool movedHead = false;
        try
        {
            wrote = true;
            workspace.Save(combined);
            string[] paths = OwnedPaths(workspace, oursSha, combined);
            workspace.Transport.ExecuteGitWithIndex(alternateIndex, ["read-tree", oursSha]);
            workspace.Transport.ExecuteGitWithIndex(alternateIndex,
                new[] { "add", "-A", "--" }.Concat(paths));
            string tree = workspace.Transport.ExecuteGitWithIndex(
                alternateIndex, ["write-tree"]).Trim();
            ValidateHash(tree);
            string sha = workspace.Transport.ExecuteGit(
                "-c", "user.name=ExtremeEditor Together",
                "-c", "user.email=extremeeditor-together@localhost.invalid",
                "commit-tree", tree, "-p", oursSha, "-p", remoteSha,
                "-m", "Merge time-first Together segments").Trim();
            ValidateHash(sha);

            if (workspace.HeadCommit != oursSha ||
                ResolveCommit(workspace, referenceToGuard) != remoteSha)
                throw new InvalidOperationException("Git refs changed during commit preparation.");
            workspace.Transport.ExecuteGit(new[] { "add", "-A", "--" }.Concat(paths).ToArray());
            workspace.Transport.ExecuteGit(
                "update-ref", "refs/heads/" + branchName, sha, oursSha);
            movedHead = true;
            return Success(remoteSha, sha);
        }
        catch (Exception error)
        {
            if (wrote && !movedHead)
            {
                try
                {
                    workspace.Save(ours);
                    workspace.Transport.ExecuteGit(new[] { "add", "-A", "--" }
                        .Concat(OwnedPaths(workspace, oursSha, ours)).ToArray());
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("Semantic merge failed and rollback was incomplete.",
                        error, rollback);
                }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(alternateIndex)) File.Delete(alternateIndex); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record SegmentPayload(SegmentContentEntry Content,
        SegmentTimeDraft Draft, TimeFirstAuthoredSegment? Authored);

    private static SegmentPayload? Payload(TimeFirstSegmentSnapshot snapshot, string id) =>
        snapshot.Content.TryGetValue(id, out SegmentContentEntry? content)
            ? new SegmentPayload(content, snapshot.Drafts[id],
                snapshot.Authored is not null && snapshot.Authored.TryGetValue(id,
                    out TimeFirstAuthoredSegment? authored) ? authored : null)
            : null;

    private static bool PayloadEquals(SegmentPayload? left, SegmentPayload? right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static T? Choose<T>(T? ancestor, T? left, T? right,
        Func<T?, T?, bool> equals, string key, SortedSet<string> conflicts)
    {
        if (equals(left, right)) return left;
        if (equals(ancestor, left)) return right;
        if (equals(ancestor, right)) return left;
        conflicts.Add(key);
        return default;
    }

    private static TimeFirstSegmentPullResult Success(string remote, string local) =>
        new(remote, local, [], []);

    private static string[] OwnedPaths(
        TimeFirstSegmentGitWorkspace workspace, string ancestorSha,
        TimeFirstSegmentSnapshot snapshot)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal)
        {
            SegmentGitWorkspace.MarkerFileName, MarkerName, ProjectName
        };
        foreach (string id in snapshot.Content.Keys)
            paths.Add(FolderName + "/" + id + ".json");
        string tracked = workspace.Transport.ExecuteGit(
            "ls-tree", "-r", "--name-only", ancestorSha, "--", FolderName);
        foreach (string path in tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = path.TrimEnd('\r');
            string prefix = FolderName + "/";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal) ||
                !normalized.EndsWith(".json", StringComparison.Ordinal)) continue;
            string id = normalized[prefix.Length..^5];
            if (id.Length == 0 || id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0]) ||
                !id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                throw new InvalidDataException("Unsafe tracked time-first Segment filename.");
            paths.Add(normalized);
        }
        return paths.ToArray();
    }

    private static string ResolveCommit(TimeFirstSegmentGitWorkspace workspace, string revision)
    {
        string sha = workspace.Transport.ExecuteGit(
            "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();
        ValidateHash(sha);
        return sha;
    }

    private static void ValidateHash(string sha)
    {
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Invalid Git object hash.");
    }
}
