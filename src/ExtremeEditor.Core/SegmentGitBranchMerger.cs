using System.Text.Json;

namespace ExtremeEditor.Core;

/// <summary>
/// The result of merging a local collaboration branch into the checked-out
/// collaboration branch. Conflicted merges never return an updated HEAD.
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
/// Bridges a real Git merge-base and two committed chart snapshots with
/// domain-specific timeline and whole-Segment content merges. The result is
/// a two-parent commit, built through Git plumbing without invoking
/// 'git merge' or leaving conflict markers in the worktree.
/// </summary>
public static class SegmentGitBranchMerger
{
    public static SegmentGitMergeResult MergeIntoCurrent(
        SegmentGitWorkspace workspace,
        string sourceBranch)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(sourceBranch);
        workspace.AssertCleanOwnedWorktree();

        string currentBranch = workspace.CurrentBranch;
        SegmentGitWorkspace.ValidateBranchName(currentBranch);
        if (string.Equals(currentBranch, sourceBranch, StringComparison.Ordinal))
            throw new ArgumentException("Cannot merge a branch into itself.", nameof(sourceBranch));

        string oursSha = ResolveCommit(workspace, "refs/heads/" + currentBranch);
        string theirsSha = ResolveCommit(workspace, "refs/heads/" + sourceBranch);
        string[] bases = workspace.ExecuteGit("merge-base", "--all", oursSha, theirsSha)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (bases.Length != 1)
            throw new InvalidOperationException(
                "The merge requires exactly one shared Git ancestor.");

        string baseSha = bases[0];
        // Fast-forward and already-merged histories are intentionally outside
        // this first PoC: no unnecessary two-parent commits or ref rewrites.
        if (baseSha == oursSha || baseSha == theirsSha)
            throw new InvalidOperationException(
                "This PoC requires two divergent branches; fast-forward/already-merged is not supported.");

        SegmentGitSnapshot common = workspace.ReadAt(baseSha);
        SegmentGitSnapshot ours = workspace.ReadAt(oursSha);
        SegmentGitSnapshot theirs = workspace.ReadAt(theirsSha);

        SegmentTimeMergeResult timelineMerge = SegmentTimeThreeWayMerger.Merge(
            common.Timeline, ours.Timeline, theirs.Timeline);
        (Dictionary<string, LevelSegment> mergedSegments, string[] contentConflicts) =
            MergeContent(common.Segments, ours.Segments, theirs.Segments);

        if (!timelineMerge.Success || contentConflicts.Length != 0)
            return new SegmentGitMergeResult(
                baseSha, null, timelineMerge.Conflicts.ToArray(), contentConflicts);

        SegmentTimeTimeline timeline = timelineMerge.Timeline!;
        var allowed = timeline.Partitions
            .Select(static part => part.SegmentId).ToHashSet(StringComparer.Ordinal);
        // A deliberate deletion from the timeline also retires the matching
        // content file. Never resurrect content whose assignment was removed.
        foreach (string id in mergedSegments.Keys.Where(id => !allowed.Contains(id)).ToArray())
            mergedSegments.Remove(id);
        var combined = new SegmentGitSnapshot(timeline, mergedSegments);
        SegmentGitWorkspace.ValidateSnapshot(combined);

        // Recheck both refs before ever changing worktree state.
        // update-ref's expected-old-SHA check protects the final branch move.
        if (ResolveCommit(workspace, "refs/heads/" + currentBranch) != oursSha ||
            ResolveCommit(workspace, "refs/heads/" + sourceBranch) != theirsSha)
            throw new InvalidOperationException("Branch heads changed while preparing the merge.");
        workspace.AssertCleanOwnedWorktree();

        string alternateIndex = Path.Combine(
            workspace.Directory, ".git",
            "extremeeditor-merge-index-" + Guid.NewGuid().ToString("N"));
        bool modifyingWorktree = false;
        bool movedHead = false;
        try
        {
            modifyingWorktree = true;
            workspace.Save(combined);

            // All eligible paths are chosen explicitly. A Git index built
            // from the current HEAD excludes unrelated untracked/staged data.
            // Old tracked segment paths are included to stage safe deletions.
            string[] paths = OwnedPaths(workspace, oursSha, combined);
            workspace.ExecuteGitWithIndex(alternateIndex,
                ["read-tree", oursSha]);
            workspace.ExecuteGitWithIndex(alternateIndex,
                new[] { "add", "-A", "--" }.Concat(paths));
            string treeSha = workspace.ExecuteGitWithIndex(
                alternateIndex, ["write-tree"]).Trim();
            ValidateHash(treeSha);

            string message = $"Merge {sourceBranch} into {currentBranch} (ExtremeEditor Together)";
            string mergeSha = workspace.ExecuteGit(
                "-c", "user.name=ExtremeEditor Together",
                "-c", "user.email=extremeeditor-together@localhost.invalid",
                "commit-tree", treeSha,
                "-p", oursSha,
                "-p", theirsSha,
                "-m", message).Trim();
            ValidateHash(mergeSha);

            // Sync ONLY collaboration-owned files in the normal index before
            // atomically moving the branch. Other staged files stay untouched.
            workspace.ExecuteGit(new[] { "add", "-A", "--" }
                .Concat(paths).ToArray());
            if (ResolveCommit(workspace, "refs/heads/" + sourceBranch) != theirsSha)
                throw new InvalidOperationException("Source branch moved during merge.");
            workspace.ExecuteGit(
                "update-ref", "refs/heads/" + currentBranch, mergeSha, oursSha);
            movedHead = true;

            return new SegmentGitMergeResult(
                baseSha, mergeSha,
                Array.Empty<SegmentTimeMergeConflict>(),
                Array.Empty<string>());
        }
        catch (Exception mergeError)
        {
            if (modifyingWorktree && !movedHead)
            {
                // We started with a clean collaboration worktree. Restore the
                // pre-merge snapshot and owned index paths, with no reset,
                // checkout, stash or clean. Preserve the original exception.
                try
                {
                    workspace.Save(ours);
                    string[] paths = OwnedPaths(workspace, oursSha, ours);
                    workspace.ExecuteGit(new[] { "add", "-A", "--" }
                        .Concat(paths).ToArray());
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "Merge failed and rollback was incomplete. Inspect the isolated collaboration workspace; no reset was attempted.",
                        mergeError, rollbackError);
                }
            }
            throw;
        }
        finally
        {
            // Alternate index is temporary and lives inside this dedicated
            // .git directory, not in the caller's source-code repository.
            // Failure to remove it must not mask success or earlier failures.
            try
            {
                if (File.Exists(alternateIndex))
                    File.Delete(alternateIndex);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (Dictionary<string, LevelSegment> Content, string[] Conflicts)
        MergeContent(
            IReadOnlyDictionary<string, LevelSegment> original,
            IReadOnlyDictionary<string, LevelSegment> ours,
            IReadOnlyDictionary<string, LevelSegment> theirs)
    {
        var ids = new SortedSet<string>(original.Keys, StringComparer.Ordinal);
        ids.UnionWith(ours.Keys);
        ids.UnionWith(theirs.Keys);
        var merged = new Dictionary<string, LevelSegment>(StringComparer.Ordinal);
        var conflicts = new List<string>();

        foreach (string id in ids)
        {
            original.TryGetValue(id, out LevelSegment? initial);
            ours.TryGetValue(id, out LevelSegment? left);
            theirs.TryGetValue(id, out LevelSegment? right);

            // Compare the complete serialized payload: angle data, events,
            // nested JSON, decoration properties, source indices and ranges.
            // Metadata ContentRevisionId is explicitly NOT trusted to
            // establish content identity.
            bool sameSides = ContentEquals(left, right);
            bool leftUnchanged = ContentEquals(left, initial);
            bool rightUnchanged = ContentEquals(right, initial);

            LevelSegment? chosen;
            if (sameSides)
                chosen = left;
            else if (leftUnchanged)
                chosen = right;
            else if (rightUnchanged)
                chosen = left;
            else
            {
                conflicts.Add(id);
                continue;
            }
            if (chosen is not null)
                merged.Add(id, chosen);
        }

        return (merged, conflicts.ToArray());
    }

    private static bool ContentEquals(LevelSegment? a, LevelSegment? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        // Conservative for this PoC: serialization differences are conflicts,
        // rather than attempting unsafe fieldwise ADOFAI event merging.
        return string.Equals(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b),
            StringComparison.Ordinal);
    }

    private static string[] OwnedPaths(
        SegmentGitWorkspace workspace, string originalCommit,
        SegmentGitSnapshot combined)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal)
        {
            SegmentGitWorkspace.MarkerFileName,
            SegmentGitWorkspace.TimelineFileName
        };
        foreach (string id in combined.Segments.Keys)
            paths.Add(SegmentGitWorkspace.SegmentFileName(id));

        string tracked = workspace.ExecuteGit(
            "ls-tree", "-r", "--name-only", originalCommit, "--",
            SegmentGitWorkspace.SegmentsFolderName);
        foreach (string path in tracked.Split(
                     new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string prefix = SegmentGitWorkspace.SegmentsFolderName + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.Ordinal))
                continue;
            string id = path[prefix.Length..^5];
            // Validate a tracked path before using it as a pathspec.
            paths.Add(SegmentGitWorkspace.SegmentFileName(id));
        }
        return paths.ToArray();
    }

    private static string ResolveCommit(SegmentGitWorkspace workspace, string branchRef)
    {
        string sha = workspace.ExecuteGit(
            "rev-parse", "--verify", "--end-of-options", branchRef + "^{commit}").Trim();
        ValidateHash(sha);
        return sha;
    }

    private static void ValidateHash(string sha)
    {
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Git returned an invalid object ID.");
    }
}
