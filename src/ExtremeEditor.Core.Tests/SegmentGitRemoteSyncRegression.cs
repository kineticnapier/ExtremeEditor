using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Two genuine local clients with a local bare repository acting as origin.
/// No network or developer source repository is accessed by this regression.
/// </summary>
internal static class SegmentGitRemoteSyncRegression
{
    private const int SampleRate = 48_000;
    private static long S(int seconds) => (long)seconds * SampleRate;

    public static void Run()
    {
        string successful = NewTestRoot();
        string conflicting = NewTestRoot();
        string overlapping = NewTestRoot();
        try
        {
            ConcurrentClientsCanSynchronize(successful);
            ConflictingClientEditsAreNotLost(conflicting);
            OverlappingTimelineAssignmentsConflict(overlapping);
        }
        finally
        {
            CleanupTestRoot(successful);
            CleanupTestRoot(conflicting);
            CleanupTestRoot(overlapping);
        }
    }

    private static void ConcurrentClientsCanSynchronize(string root)
    {
        Directory.CreateDirectory(root);
        string bare = CreateBare(root);
        string aDirectory = Path.Combine(root, "client-A");
        string bDirectory = Path.Combine(root, "client-B");

        SegmentGitWorkspace a = SegmentGitWorkspace.Initialize(aDirectory);
        a.Save(BaseSnapshot());
        string baseCommit = a.Commit("initial shared segments");

        SegmentGitRemoteSync.ConnectLocalBare(a, bare);

        // Reject an existing non-bare workspace as origin. Only a dedicated
        // explicitly supplied bare repository may be used for this PoC.
        ExpectRejected(() => SegmentGitRemoteSync.ConnectLocalBare(a, aDirectory),
            "a developer or working Git directory cannot be a remote");
        string pushedBase = SegmentGitRemoteSync.Push(a);
        Equal(baseCommit, pushedBase, "first push publishes base commit");
        Equal(baseCommit, BareHead(bare), "bare main contains initial commit");

        string unsafeDestination = Path.Combine(root, "existing");
        Directory.CreateDirectory(unsafeDestination);
        File.WriteAllText(Path.Combine(unsafeDestination, "keep.txt"), "never overwrite");
        ExpectRejected(
            () => SegmentGitRemoteSync.CloneLocalBare(bare, unsafeDestination),
            "clone must refuse nonempty existing destinations");
        Equal("never overwrite",
            File.ReadAllText(Path.Combine(unsafeDestination, "keep.txt")),
            "clone refuses to damage existing files");

        SegmentGitWorkspace b = SegmentGitRemoteSync.CloneLocalBare(bare, bDirectory);
        Equal("main", b.CurrentBranch, "clone starts on main");
        Equal(baseCommit, Head(bDirectory), "clone reads published base commit");
        AssertContent(b.Load(), "A", 0, "A-r1");
        AssertContent(b.Load(), "B", 45, "B-r1");

        SaveEditedContent(a, "A", 90, "A-r2");
        string aCommit = a.Commit("client A edits A");
        Equal(aCommit, SegmentGitRemoteSync.Push(a), "client A publishes its edit");
        Equal(aCommit, BareHead(bare), "bare remote advanced to A");

        SaveEditedContent(b, "B", 270, "B-r2");
        string bCommit = b.Commit("client B edits B");
        string localBefore = Head(bDirectory);
        Check(localBefore == bCommit && bCommit != aCommit,
            "client B has an independent committed history");

        // Never force-push divergent work and overwrite another collaborator.
        ExpectRejected(() => SegmentGitRemoteSync.Push(b),
            "divergent non-fast-forward push must be rejected");
        Equal(aCommit, BareHead(bare), "failed push preserves published A commit");
        Equal(bCommit, Head(bDirectory), "failed push preserves B HEAD");

        string notePath = Path.Combine(bDirectory, "local-only-notes.txt");
        File.WriteAllText(notePath, "not part of the shared chart");
        byte[] bTimelineBefore = File.ReadAllBytes(
            Path.Combine(bDirectory, "timeline.json"));
        byte[] bContentBefore = File.ReadAllBytes(
            Path.Combine(bDirectory, "segments", "B.json"));

        string fetched = SegmentGitRemoteSync.Fetch(b);
        Equal(aCommit, fetched, "fetch updates origin/main to remote A");
        Equal(aCommit,
            RunGit(bDirectory, "rev-parse", "refs/remotes/origin/main").Trim(),
            "tracking ref updated");
        Equal(bCommit, Head(bDirectory), "fetch does not change local HEAD");
        Sequence(bTimelineBefore,
            File.ReadAllBytes(Path.Combine(bDirectory, "timeline.json")),
            "fetch leaves timeline untouched");
        Sequence(bContentBefore,
            File.ReadAllBytes(Path.Combine(bDirectory, "segments", "B.json")),
            "fetch leaves Segment B untouched");

        SegmentGitPullResult merged = SegmentGitRemoteSync.Pull(b);
        Check(merged.Success, "different Segment edits synchronize without conflict");
        Equal(aCommit, merged.RemoteCommit, "pull identifies fetched remote revision");
        Check(IsSha(merged.LocalCommit), "pull produced a valid local commit");
        Equal(merged.LocalCommit, Head(bDirectory),
            "pull advances only the local checked-out branch");
        Equal("main", b.CurrentBranch, "pull stays on main");

        string[] parents = RunGit(bDirectory, "show", "-s", "--format=%P", "HEAD")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Equal(2, parents.Length, "divergent pull produces a real two-parent commit");
        Equal(bCommit, parents[0], "first parent is client B's previous HEAD");
        Equal(aCommit, parents[1], "second parent is fetched client A history");
        AssertContent(b.Load(), "A", 90, "A-r2");
        AssertContent(b.Load(), "B", 270, "B-r2");
        Equal("not part of the shared chart", File.ReadAllText(notePath),
            "unrelated files survive fetch and merge");
        Check(!RunGit(bDirectory, "ls-tree", "-r", "--name-only", "HEAD")
            .Contains("local-only-notes.txt", StringComparison.Ordinal),
            "unrelated untracked files must not enter the merge commit");

        Equal(aCommit, BareHead(bare),
            "pull is local only and must not implicitly publish");
        string mergedCommit = SegmentGitRemoteSync.Push(b);
        Equal(merged.LocalCommit, mergedCommit, "push publishes semantic merge");
        Equal(mergedCommit, BareHead(bare), "remote now holds both authors' edits");

        // Client A can catch up by fast-forwarding; don't invent a duplicate
        // merge commit or lose B's work. Fetch itself cannot change HEAD.
        string aFetched = SegmentGitRemoteSync.Fetch(a);
        Equal(mergedCommit, aFetched, "A fetches B's published merge");
        Equal(aCommit, Head(aDirectory), "A fetch does not touch local HEAD");
        SegmentGitPullResult caughtUp = SegmentGitRemoteSync.Pull(a);
        Check(caughtUp.Success, "client A can fast-forward to the shared result");
        Equal(mergedCommit, caughtUp.LocalCommit,
            "fast-forward uses the published commit without rewriting history");
        Equal(mergedCommit, Head(aDirectory), "client A reaches common HEAD");
        AssertContent(a.Load(), "A", 90, "A-r2");
        AssertContent(a.Load(), "B", 270, "B-r2");
        Equal("main", a.CurrentBranch, "fast-forward keeps local branch checked out");
    }

    private static void ConflictingClientEditsAreNotLost(string root)
    {
        Directory.CreateDirectory(root);
        string bare = CreateBare(root);
        string aDirectory = Path.Combine(root, "client-A");
        string bDirectory = Path.Combine(root, "client-B");
        SegmentGitWorkspace a = SegmentGitWorkspace.Initialize(aDirectory);
        a.Save(BaseSnapshot());
        a.Commit("base");
        SegmentGitRemoteSync.ConnectLocalBare(a, bare);
        SegmentGitRemoteSync.Push(a);
        SegmentGitWorkspace b = SegmentGitRemoteSync.CloneLocalBare(bare, bDirectory);

        // Both change actual content A, but leave ContentRevisionId as A-r1.
        // A metadata-only merge would incorrectly treat this as unchanged.
        SaveEditedContent(a, "A", 90, revision: null);
        string aCommit = a.Commit("A modifies same segment");
        SegmentGitRemoteSync.Push(a);

        SaveEditedContent(b, "A", 135, revision: null);
        string bCommit = b.Commit("B independently modifies same segment");
        byte[] bTimeline = File.ReadAllBytes(Path.Combine(bDirectory, "timeline.json"));
        byte[] bContent = File.ReadAllBytes(Path.Combine(bDirectory, "segments", "A.json"));
        byte[] originalIndex = File.ReadAllBytes(Path.Combine(bDirectory, ".git", "index"));
        string beforeStatus = RunGit(bDirectory,
            "status", "--porcelain", "--untracked-files=all");

        SegmentGitPullResult conflict = SegmentGitRemoteSync.Pull(b);
        Check(!conflict.Success && conflict.LocalCommit is null,
            "divergent content cannot silently merge");
        Equal(aCommit, conflict.RemoteCommit, "conflict identifies fetched A revision");
        Check(conflict.ContentConflictSegmentIds.Contains("A", StringComparer.Ordinal),
            "conflict identifies the actual changed content A");
        Equal(bCommit, Head(bDirectory), "conflicting pull does not move B HEAD");
        Equal(aCommit, BareHead(bare), "conflicting pull never changes remote history");
        Sequence(bTimeline, File.ReadAllBytes(Path.Combine(bDirectory, "timeline.json")),
            "conflicting pull leaves timeline bytes unchanged");
        Sequence(bContent, File.ReadAllBytes(Path.Combine(bDirectory, "segments", "A.json")),
            "conflicting pull leaves content bytes unchanged");
        Sequence(originalIndex,
            File.ReadAllBytes(Path.Combine(bDirectory, ".git", "index")),
            "conflicting pull leaves the Git index unchanged");
        Equal(beforeStatus,
            RunGit(bDirectory, "status", "--porcelain", "--untracked-files=all"),
            "conflicting pull leaves worktree status unchanged");
        Check(!File.Exists(Path.Combine(bDirectory, ".git", "MERGE_HEAD")),
            "conflict does not start a native Git merge");

        // A draft made after the conflict is not eligible for implicit
        // overwrite, even when remote tracking data already exists.
        SaveEditedContent(b, "B", 180, "B-draft");
        byte[] draft = File.ReadAllBytes(Path.Combine(bDirectory, "segments", "B.json"));
        string dirtyBefore = RunGit(bDirectory,
            "status", "--porcelain", "--untracked-files=all");
        ExpectRejected(() => SegmentGitRemoteSync.Pull(b),
            "pull must refuse uncommitted collaboration edits");
        Equal(bCommit, Head(bDirectory), "dirty pull rejection preserves HEAD");
        Sequence(draft, File.ReadAllBytes(Path.Combine(bDirectory, "segments", "B.json")),
            "dirty pull rejection preserves the draft");
        Equal(dirtyBefore,
            RunGit(bDirectory, "status", "--porcelain", "--untracked-files=all"),
            "dirty pull rejection preserves index and worktree status");

        // Fetch remains safe even if the working tree is dirty: it must only
        // update remote-tracking refs and never write over draft content.
        Equal(aCommit, SegmentGitRemoteSync.Fetch(b),
            "fetch is allowed to inspect remote history while dirty");
        Sequence(draft, File.ReadAllBytes(Path.Combine(bDirectory, "segments", "B.json")),
            "fetch cannot alter an uncommitted draft");
    }

    private static void OverlappingTimelineAssignmentsConflict(string root)
    {
        Directory.CreateDirectory(root);
        string bare = CreateBare(root);
        string aDirectory = Path.Combine(root, "client-A");
        string bDirectory = Path.Combine(root, "client-B");
        SegmentGitWorkspace a = SegmentGitWorkspace.Initialize(aDirectory);
        a.Save(BaseSnapshot());
        a.Commit("base");
        SegmentGitRemoteSync.ConnectLocalBare(a, bare);
        SegmentGitRemoteSync.Push(a);
        SegmentGitWorkspace b = SegmentGitRemoteSync.CloneLocalBare(bare, bDirectory);

        SegmentGitSnapshot left = a.Load();
        a.Save(left with
        {
            Timeline = SegmentTimePlanner.CreateEmpty(
                left.Timeline, "X", S(20), S(30), "user-A")
        });
        string aCommit = a.Commit("A creates X");
        SegmentGitRemoteSync.Push(a);

        SegmentGitSnapshot right = b.Load();
        b.Save(right with
        {
            Timeline = SegmentTimePlanner.CreateEmpty(
                right.Timeline, "Y", S(25), S(35), "user-B")
        });
        string bCommit = b.Commit("B creates Y");
        byte[] before = File.ReadAllBytes(Path.Combine(bDirectory, "timeline.json"));

        SegmentGitPullResult conflict = SegmentGitRemoteSync.Pull(b);
        Check(!conflict.Success && conflict.LocalCommit is null,
            "overlapping active intervals cannot be merged");
        Check(conflict.TimelineConflicts.Any(c =>
                c.Kind == SegmentTimeMergeConflictKind.ActiveRangeOverlap &&
                ((c.SegmentId == "X" && c.OtherSegmentId == "Y") ||
                 (c.SegmentId == "Y" && c.OtherSegmentId == "X"))),
            "timeline conflict identifies both overlapping Segment IDs");
        Equal(aCommit, conflict.RemoteCommit, "remote overlap commit detected");
        Equal(bCommit, Head(bDirectory), "overlap keeps B HEAD");
        Equal(aCommit, BareHead(bare), "overlap leaves the remote untouched");
        Sequence(before, File.ReadAllBytes(Path.Combine(bDirectory, "timeline.json")),
            "overlap leaves B timeline unchanged");
    }

    private static SegmentGitSnapshot BaseSnapshot()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(5);
        level.Angles = [0d, 45d, 999d, 90d];
        level.RebuildGeometry();
        LevelSegment[] segments = LevelSegmentPartitioner.Split(level, 2).ToArray();
        return new SegmentGitSnapshot(
            new SegmentTimeTimeline(SampleRate,
            [
                new("A", S(0), S(10), "A-r1", S(0),
                    "user-A", SegmentPartitionStatus.Active),
                new("B", S(10), S(20), "B-r1", S(10),
                    "user-B", SegmentPartitionStatus.Active)
            ]),
            new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
            {
                ["A"] = segments[0],
                ["B"] = segments[1]
            });
    }

    private static void SaveEditedContent(
        SegmentGitWorkspace workspace, string id, double angle, string? revision)
    {
        SegmentGitSnapshot old = workspace.Load();
        var changedSegments = old.Segments.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        LevelSegment original = changedSegments[id];
        double[] angles = original.DestinationAngles.ToArray();
        angles[0] = angle;
        changedSegments[id] = original with { DestinationAngles = angles };
        var timeline = old.Timeline with
        {
            Partitions = old.Timeline.Partitions.Select(part =>
                part.SegmentId == id && revision is not null
                    ? part with { ContentRevisionId = revision }
                    : part).ToArray()
        };
        workspace.Save(new SegmentGitSnapshot(timeline, changedSegments));
    }

    private static void AssertContent(
        SegmentGitSnapshot snapshot, string segmentId, double firstAngle, string revision)
    {
        Equal(firstAngle, snapshot.Segments[segmentId].DestinationAngles[0],
            $"content angle of {segmentId}");
        Equal(revision, snapshot.Timeline.Partitions.Single(p =>
            p.SegmentId == segmentId).ContentRevisionId,
            $"content revision of {segmentId}");
    }

    private static string CreateBare(string parent)
    {
        string bare = Path.Combine(parent, "origin.git");
        RunGit(parent, "init", "--bare", "--initial-branch=main", bare);
        Check(Directory.Exists(Path.Combine(bare, "objects")) &&
              File.Exists(Path.Combine(bare, "HEAD")),
            "real bare Git remote created in private temp directory");
        return bare;
    }

    private static string BareHead(string bare) =>
        RunGit(bare, "rev-parse", "--verify", "refs/heads/main").Trim();

    private static string Head(string repository) =>
        RunGit(repository, "rev-parse", "--verify", "HEAD").Trim();

    private static string NewTestRoot() =>
        Path.Combine(Path.GetTempPath(),
            $"extremeeditor-remote-sync-{Guid.NewGuid():N}");

    private static string RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not launch local Git.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Local Git command timed out.");
        }
        string errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed: {errors}");
        return stdout;
    }

    private static void CleanupTestRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            // Never traverse links/junctions. Delete ONLY the unique test root.
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                FileAttributes attrs = File.GetAttributes(directory);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to traverse linked test directory.");
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing to traverse linked test entry.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(directory, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"WARNING: isolated test cleanup did not complete for '{root}': {ex.Message}");
        }
    }

    private static void ExpectRejected(Action action, string description)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Unsafe remote sync accepted: " + description);
    }

    private static bool IsSha(string? value) =>
        value is not null && value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string description)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(description);
    }
}
