using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Isolated local Git integration test: each repo is a freshly created
/// temporary collaboration workspace, never the ExtremeEditor source repo.
/// </summary>
internal static class SegmentGitBranchMergeRegression
{
    private const int SampleRate = 48_000;
    private static long S(int seconds) => (long)seconds * SampleRate;

    public static void Run()
    {
        string clean = NewTestRoot();
        string contentConflict = NewTestRoot();
        string overlapConflict = NewTestRoot();
        try
        {
            IndependentEditsCreateTwoParentMergeCommit(clean);
            ConflictingContentPreservesBothBranches(contentConflict);
            OverlappingNewIntervalsPreserveBothBranches(overlapConflict);
        }
        finally
        {
            CleanupTestRepository(clean);
            CleanupTestRepository(contentConflict);
            CleanupTestRepository(overlapConflict);
        }
    }

    private static void IndependentEditsCreateTwoParentMergeCommit(string root)
    {
        SegmentGitWorkspace workspace = SegmentGitWorkspace.Initialize(root);
        workspace.Save(BaseSnapshot());
        string baseSha = workspace.Commit("base");
        workspace.CreateBranch("work/A");
        workspace.CreateBranch("work/B");

        workspace.Checkout("work/A");
        SaveEditedContent(workspace, "A", 45, "A-r2");
        string aHead = workspace.Commit("A edits segment A");

        workspace.Checkout("work/B");
        SaveEditedContent(workspace, "B", 270, "B-r2");
        string bHead = workspace.Commit("B edits segment B");

        // Demonstrate that git merge-base -- not a supplied input -- identifies
        // this common ancestor. Save the expected SHA before calling the API.
        Equal(baseSha,
            RunGit(root, "merge-base", "work/A", "work/B").Trim(),
            "Git merge-base is the actual shared base");

        string notes = Path.Combine(root, "untracked-local-note.txt");
        File.WriteAllText(notes, "must remain untracked and unchanged");

        SegmentGitMergeResult result =
            SegmentGitBranchMerger.MergeIntoCurrent(workspace, "work/A");

        Check(result.Success, "independent modifications should auto-merge");
        Equal(baseSha, result.BaseCommit, "merger discovers the shared ancestor");
        Check(IsSha(result.MergeCommit), "returns the created Git merge commit SHA");
        Equal(result.MergeCommit, RunGit(root, "rev-parse", "HEAD").Trim(),
            "merged branch HEAD points at merge commit");
        Equal("work/B", workspace.CurrentBranch, "merger leaves checked-out branch work/B");

        string[] parents = RunGit(root, "show", "-s", "--format=%P", "HEAD")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Equal(2, parents.Length, "real Git merge commit has exactly two parents");
        Equal(bHead, parents[0], "first parent is ours");
        Equal(aHead, parents[1], "second parent is theirs");
        Equal(aHead, RunGit(root, "rev-parse", "work/A").Trim(),
            "source branch must not be moved");

        SegmentGitSnapshot combined = workspace.Load();
        AssertContent(combined, "A", 45, "A-r2");
        AssertContent(combined, "B", 270, "B-r2");
        SegmentTimePlanner.Validate(combined.Timeline);
        AssertContent(workspace.ReadAt(result.MergeCommit!), "A", 45, "A-r2");
        AssertContent(workspace.ReadAt(result.MergeCommit!), "B", 270, "B-r2");
        AssertContent(workspace.ReadAt(aHead), "B", 45, "B-r1");
        AssertContent(workspace.ReadAt(bHead), "A", 0, "A-r1");

        Equal("must remain untracked and unchanged", File.ReadAllText(notes),
            "unrelated untracked data must survive merge");
        Check(!RunGit(root, "ls-tree", "-r", "--name-only", "HEAD")
                .Contains("untracked-local-note.txt", StringComparison.Ordinal),
            "merge commit cannot accidentally include unrelated files");
        Check(!File.Exists(Path.Combine(root, ".git", "MERGE_HEAD")),
            "successful semantic merge leaves no unfinished native Git merge state");
    }

    private static void ConflictingContentPreservesBothBranches(string root)
    {
        SegmentGitWorkspace workspace = SegmentGitWorkspace.Initialize(root);
        workspace.Save(BaseSnapshot());
        workspace.Commit("base");
        workspace.CreateBranch("work/A");
        workspace.CreateBranch("work/B");

        workspace.Checkout("work/A");
        // Intentionally keep the SAME revision ID on both branches. The
        // merge must compare actual LevelSegment content, not metadata alone.
        SaveEditedContent(workspace, "A", 45, revision: null);
        string aHead = workspace.Commit("A makes one content edit");

        workspace.Checkout("work/B");
        SaveEditedContent(workspace, "A", 90, revision: null);
        string bHead = workspace.Commit("B makes a different content edit");

        byte[] beforeTimeline = File.ReadAllBytes(Path.Combine(root, "timeline.json"));
        byte[] beforeContent = File.ReadAllBytes(Path.Combine(root, "segments", "A.json"));
        string statusBefore = RunGit(root, "status", "--porcelain", "--untracked-files=all");

        SegmentGitMergeResult result =
            SegmentGitBranchMerger.MergeIntoCurrent(workspace, "work/A");
        Check(!result.Success, "divergent same-ID content must conflict");
        Check(result.MergeCommit is null, "conflict must not return a partial merge commit");
        Check(result.ContentConflictSegmentIds.Contains("A", StringComparer.Ordinal),
            "actual segment content conflict must identify A");
        Equal(bHead, RunGit(root, "rev-parse", "HEAD").Trim(),
            "conflict preserves the current HEAD");
        Equal(aHead, RunGit(root, "rev-parse", "work/A").Trim(),
            "conflict preserves the source branch");
        Equal("work/B", workspace.CurrentBranch, "conflict does not switch branch");
        Equal(statusBefore,
            RunGit(root, "status", "--porcelain", "--untracked-files=all"),
            "conflict preserves index and worktree status exactly");
        Sequence(beforeTimeline, File.ReadAllBytes(Path.Combine(root, "timeline.json")),
            "conflict leaves timeline bytes unchanged");
        Sequence(beforeContent, File.ReadAllBytes(Path.Combine(root, "segments", "A.json")),
            "conflict leaves A content bytes unchanged");
        AssertContent(workspace.Load(), "A", 90, "A-r1");
        AssertContent(workspace.ReadAt(aHead), "A", 45, "A-r1");
        Check(!File.Exists(Path.Combine(root, ".git", "MERGE_HEAD")),
            "conflict must not leave native Git merge in progress");

        // Even a non-conflicting merge must NEVER wipe uncommitted edits.
        SaveEditedContent(workspace, "B", 180, "B-draft");
        string dirtyBefore = RunGit(root, "status", "--porcelain", "--untracked-files=all");
        byte[] draft = File.ReadAllBytes(Path.Combine(root, "segments", "B.json"));
        ExpectRejected(() => SegmentGitBranchMerger.MergeIntoCurrent(workspace, "work/A"),
            "dirty owned worktree should not be merged, stashed, reset or cleaned");
        Equal(bHead, RunGit(root, "rev-parse", "HEAD").Trim(),
            "dirty rejection preserves HEAD");
        Equal(dirtyBefore,
            RunGit(root, "status", "--porcelain", "--untracked-files=all"),
            "dirty rejection preserves index and worktree status");
        Sequence(draft, File.ReadAllBytes(Path.Combine(root, "segments", "B.json")),
            "dirty worktree content cannot be discarded");
    }

    private static void OverlappingNewIntervalsPreserveBothBranches(string root)
    {
        SegmentGitWorkspace workspace = SegmentGitWorkspace.Initialize(root);
        workspace.Save(BaseSnapshot());
        workspace.Commit("base");
        workspace.CreateBranch("work/A");
        workspace.CreateBranch("work/B");

        workspace.Checkout("work/A");
        SegmentGitSnapshot left = workspace.Load();
        workspace.Save(left with
        {
            Timeline = SegmentTimePlanner.CreateEmpty(
                left.Timeline, "X", S(20), S(30), "user-A")
        });
        string aHead = workspace.Commit("A adds time range X");

        workspace.Checkout("work/B");
        SegmentGitSnapshot right = workspace.Load();
        workspace.Save(right with
        {
            Timeline = SegmentTimePlanner.CreateEmpty(
                right.Timeline, "Y", S(25), S(35), "user-B")
        });
        string bHead = workspace.Commit("B adds time range Y");
        byte[] originalTimeline = File.ReadAllBytes(Path.Combine(root, "timeline.json"));

        // Git can combine new X and Y because they have different Segment IDs;
        // the audio-timeline semantic validator must reject [25s,30s) overlap.
        SegmentGitMergeResult result =
            SegmentGitBranchMerger.MergeIntoCurrent(workspace, "work/A");
        Check(!result.Success && result.MergeCommit is null,
            "independently valid overlapping assignments must conflict");
        Check(result.TimelineConflicts.Any(c =>
                c.Kind == SegmentTimeMergeConflictKind.ActiveRangeOverlap &&
                ((c.SegmentId == "X" && c.OtherSegmentId == "Y") ||
                 (c.SegmentId == "Y" && c.OtherSegmentId == "X"))),
            "time overlap must be reported with both conflicting segment IDs");
        Equal(bHead, RunGit(root, "rev-parse", "HEAD").Trim(),
            "time conflict keeps HEAD");
        Equal(aHead, RunGit(root, "rev-parse", "work/A").Trim(),
            "time conflict keeps source branch");
        Sequence(originalTimeline,
            File.ReadAllBytes(Path.Combine(root, "timeline.json")),
            "time conflict leaves worktree untouched");
        Check(!File.Exists(Path.Combine(root, ".git", "MERGE_HEAD")),
            "time conflict never invokes an unfinished native merge");
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
        SegmentGitSnapshot snapshot = workspace.Load();
        var segments = snapshot.Segments.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        LevelSegment before = segments[id];
        double[] angles = before.DestinationAngles.ToArray();
        angles[0] = angle;
        segments[id] = before with { DestinationAngles = angles };
        var timeline = snapshot.Timeline with
        {
            Partitions = snapshot.Timeline.Partitions.Select(p =>
                p.SegmentId == id && revision is not null
                    ? p with { ContentRevisionId = revision }
                    : p).ToArray()
        };
        workspace.Save(new SegmentGitSnapshot(timeline, segments));
    }

    private static void AssertContent(
        SegmentGitSnapshot snapshot, string id, double angle, string revision)
    {
        Equal(angle, snapshot.Segments[id].DestinationAngles[0],
            $"segment {id} angle");
        Equal(revision, snapshot.Timeline.Partitions
            .Single(p => p.SegmentId == id).ContentRevisionId,
            $"segment {id} revision");
    }

    private static string NewTestRoot() =>
        Path.Combine(Path.GetTempPath(), $"extremeeditor-git-merge-{Guid.NewGuid():N}");

    private static string RunGit(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in args)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not launch Git test process.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Git subprocess timed out.");
        }
        string err = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)}: {err}");
        return stdout;
    }

    private static void CleanupTestRepository(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            // Git loose objects can be marked read-only on Windows.
            // Enumerate only files under this unique test temp directory and
            // refuse reparse points (junctions, symbolic links) entirely.
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked test directory.");
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attr = File.GetAttributes(entry);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing linked test entry.");
                    if ((attr & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attr & ~FileAttributes.ReadOnly);
                }
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"WARNING: could not clean up isolated test repository '{root}': {e.Message}");
        }
    }

    private static bool IsSha(string? sha) =>
        sha is not null && sha.Length == 40 && sha.All(char.IsAsciiHexDigit);

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Sequence differs: {description}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
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
        throw new InvalidOperationException($"Unsafe Git merge was accepted: {description}");
    }
}
