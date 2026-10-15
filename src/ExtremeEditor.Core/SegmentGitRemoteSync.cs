using System.Diagnostics;
using System.Text;

namespace ExtremeEditor.Core;

/// <summary>
/// Result of fetching and incorporating a remote branch. A semantic conflict
/// preserves the checked-out local branch, index and worktree.
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
/// Local-only Git transport PoC. The remote must be a trusted, explicitly
/// selected bare repository on the same machine or accessible local volume.
/// This class does not support URL/SSH/HTTP remotes or external credentials.
/// </summary>
public static class SegmentGitRemoteSync
{
    private const string RemoteName = "origin";

    public static void ConnectLocalBare(
        SegmentGitWorkspace workspace, string bareRepositoryPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string bare = ValidateLocalBare(bareRepositoryPath);
        RejectNestedDestination(workspace.Directory, bare);

        // Do not silently replace an existing origin or modify global config.
        string[] remotes = workspace.ExecuteGit("remote")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (remotes.Contains(RemoteName, StringComparer.Ordinal))
        {
            string previous = workspace.ExecuteGit(
                "config", "--local", "--get", "remote.origin.url").Trim();
            if (!SamePath(previous, bare))
                throw new InvalidOperationException(
                    "This collaboration workspace already has a different origin.");
            return;
        }

        workspace.ExecuteGit("remote", "add", RemoteName, bare);
    }

    public static SegmentGitWorkspace CloneLocalBare(
        string bareRepositoryPath, string destinationDirectory)
    {
        string bare = ValidateLocalBare(bareRepositoryPath);
        string destination = NormalizeDestination(destinationDirectory);
        if (IsInside(destination, bare) || IsInside(bare, destination) ||
            SamePath(bare, destination))
            throw new ArgumentException(
                "The clone destination must be separate from the bare remote.",
                nameof(destinationDirectory));

        RejectLinkedAncestors(destination);
        RejectNestedGitRepository(destination);
        if (Directory.Exists(destination))
        {
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new ArgumentException(
                    "Clone destination must be new or empty.", nameof(destinationDirectory));
        }
        else if (File.Exists(destination))
        {
            throw new ArgumentException(
                "Clone destination cannot be an existing file.", nameof(destinationDirectory));
        }

        string parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Clone destination has no parent.", nameof(destinationDirectory));
        // Clone only from a known local bare directory. --no-hardlinks keeps
        // the two repositories' object stores independent.
        Directory.CreateDirectory(parent);
        RunGit(parent,
            "clone", "--no-hardlinks", "--no-tags",
            "--branch", "main", "--", bare, destination);

        // The marker/schema must exist in the checked-out commit. Never trust
        // an arbitrary bare repository merely because Git clone succeeds.
        SegmentGitWorkspace workspace = SegmentGitWorkspace.Open(destination);
        workspace.ReadAt("HEAD");
        string configured = ReadOrigin(workspace);
        if (!SamePath(configured, bare))
            throw new InvalidOperationException(
                "Cloned collaboration origin does not match the selected bare repository.");
        return workspace;
    }

    public static string Push(SegmentGitWorkspace workspace, string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        string bare = ReadOrigin(workspace);
        string branchRef = "refs/heads/" + branchName;
        string expected = ResolveCommit(workspace, branchRef);

        // No leading '+' refspec and no --force: Git must reject non-FF push.
        // Use the validated local directory rather than a URL or arbitrary
        // configured push destination. This publishes one explicit branch.
        workspace.ExecuteGit(
            "push", "--porcelain", "--no-tags", "--",
            bare, branchRef + ":" + branchRef);

        // A concurrent author might have advanced the remote in the interim:
        // the returned value is the commit we actually published, not a claim
        // that it will remain the bare remote's newest revision forever.
        return expected;
    }

    public static string Fetch(SegmentGitWorkspace workspace, string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        string bare = ReadOrigin(workspace);
        string branchRef = "refs/heads/" + branchName;
        string trackingRef = "refs/remotes/" + RemoteName + "/" + branchName;

        // Only this remote-tracking ref changes. '+' permits a remote rewind
        // to be *observed* but never force-pushes local author work.
        workspace.ExecuteGit(
            "fetch", "--no-tags", "--", bare,
            "+" + branchRef + ":" + trackingRef);
        return ResolveCommit(workspace, trackingRef);
    }

    public static SegmentGitPullResult Pull(
        SegmentGitWorkspace workspace, string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        if (!string.Equals(workspace.CurrentBranch, branchName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Pull must target the currently checked-out local branch.");

        // Refuse drafts before any operation that could update the worktree.
        // Fetch on its own remains available when drafts are present.
        workspace.AssertCleanOwnedWorktree();
        string remoteSha = Fetch(workspace, branchName);
        workspace.AssertCleanOwnedWorktree();

        string localRef = "refs/heads/" + branchName;
        string localSha = ResolveCommit(workspace, localRef);
        var emptyTimeline = Array.Empty<SegmentTimeMergeConflict>();
        var emptyContent = Array.Empty<string>();

        if (localSha == remoteSha)
            return new SegmentGitPullResult(remoteSha, localSha, emptyTimeline, emptyContent);

        string[] bases = workspace.ExecuteGit(
            "merge-base", "--all", localSha, remoteSha)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (bases.Length != 1)
            throw new InvalidOperationException(
                "Pull requires one common Git ancestor; multiple merge bases are unsupported.");

        if (bases[0] == remoteSha)
        {
            // Already contains the fetched remote commit. Preserve local edits
            // and avoid generating a redundant merge commit.
            return new SegmentGitPullResult(remoteSha, localSha, emptyTimeline, emptyContent);
        }

        if (bases[0] == localSha)
        {
            // History contains no divergence. Git's fast-forward-only option
            // performs its normal untracked-file collision and index checks;
            // no text merge or new commit is created.
            workspace.AssertCleanOwnedWorktree();
            workspace.ExecuteGit(
                "merge", "--ff-only", "--no-stat", remoteSha);
            return new SegmentGitPullResult(
                remoteSha, ResolveCommit(workspace, localRef),
                emptyTimeline, emptyContent);
        }

        // The existing merger resolves source commits via *local branch
        // names*. Create a unique, private, short-lived alias for the fetched
        // SHA; never move an author's branch or overwrite origin/main.
        string temporaryBranch = "extremeeditor/sync-" + Guid.NewGuid().ToString("N");
        SegmentGitWorkspace.ValidateBranchName(temporaryBranch);
        workspace.ExecuteGit("branch", "--no-track", temporaryBranch, remoteSha);
        try
        {
            // This method uses git merge-base + domain-aware 3-way merge,
            // builds a two-parent commit and protects HEAD on conflicts.
            SegmentGitMergeResult merged =
                SegmentGitBranchMerger.MergeIntoCurrent(workspace, temporaryBranch);
            return new SegmentGitPullResult(
                remoteSha, merged.MergeCommit,
                merged.TimelineConflicts.ToArray(),
                merged.ContentConflictSegmentIds.ToArray());
        }
        finally
        {
            // Only our unique ephemeral ref may be removed. Never delete a
            // collaborator's branch, or allow cleanup to hide a merge failure.
            try
            {
                workspace.ExecuteGit("branch", "-D", temporaryBranch);
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(
                    $"WARNING: temporary Git sync ref could not be deleted: {exception.Message}");
            }
        }
    }

    private static string ReadOrigin(SegmentGitWorkspace workspace)
    {
        // Read literal repository-local configuration rather than trusting a
        // shell command or accepting a URL supplied by a caller at push time.
        string configured = workspace.ExecuteGit(
            "config", "--local", "--get", "remote.origin.url").Trim();
        return ValidateLocalBare(configured);
    }

    private static string ValidateLocalBare(string directory)
    {
        string root = NormalizeDestination(directory);
        if (!Directory.Exists(root) || File.Exists(root))
            throw new ArgumentException(
                "The local bare Git repository directory does not exist.", nameof(directory));
        RejectLinkedAncestors(root);
        if (!File.Exists(Path.Combine(root, "HEAD")) ||
            !Directory.Exists(Path.Combine(root, "objects")) ||
            !File.Exists(Path.Combine(root, "config")))
            throw new ArgumentException(
                "The selected directory is not a bare Git repository.", nameof(directory));

        string isBare = RunGit(root, "rev-parse", "--is-bare-repository").Trim();
        if (!string.Equals(isBare, "true", StringComparison.Ordinal))
            throw new ArgumentException(
                "The selected Git repository must be bare.", nameof(directory));
        return root;
    }

    private static string NormalizeDestination(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A local Git directory is required.", nameof(directory));
        string full = Path.GetFullPath(directory);
        if (Path.GetPathRoot(full) == full)
            throw new ArgumentException("A filesystem root cannot be used.", nameof(directory));
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Symbolic-link/junction paths are not allowed for bare remotes or clone destinations.");
        }
    }

    private static void RejectNestedDestination(string workspace, string bare)
    {
        if (SamePath(workspace, bare) ||
            IsInside(workspace, bare) || IsInside(bare, workspace))
            throw new ArgumentException(
                "The bare remote and collaboration workspace must be separate directories.");
    }

    private static void RejectNestedGitRepository(string destination)
    {
        for (string? current = Path.GetDirectoryName(destination);
             current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) ||
                File.Exists(Path.Combine(current, ".git")))
                throw new ArgumentException(
                    "A collaboration clone cannot be nested inside another Git working tree.");
        }
    }

    private static bool IsInside(string path, string possibleParent)
    {
        string prefix = possibleParent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool SamePath(string a, string b)
    {
        if (!Path.IsPathFullyQualified(a))
            return false;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string ResolveCommit(SegmentGitWorkspace workspace, string revision)
    {
        string sha = workspace.ExecuteGit(
            "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Git returned an invalid commit ID.");
        return sha;
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        // Only used for validating/cloning from a local path. Never run a
        // shell or invoke a network-capable remote URL here.
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string env in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE",
            "GIT_COMMON_DIR", "GIT_PREFIX", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"
        })
            info.Environment.Remove(env);
        // Ensure global Git identity, aliases, hooks and remote URL rewrites
        // are not required for a test fixture to work.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.hooksPath=" +
            Path.Combine(workingDirectory, "extremeeditor-disabled-hooks"));
        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not launch local Git executable.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Local Git command timed out.");
        }
        string errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Local Git exited {process.ExitCode}: {errors.Trim()}");
        return stdout;
    }
}
