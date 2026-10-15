using System.Diagnostics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class SegmentGitWorkspaceRegression
{
    private const int SampleRate = 48_000;
    private static long S(int seconds) => (long)seconds * SampleRate;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-segment-git-{Guid.NewGuid():N}");
        string unsafeRoot = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-segment-existing-{Guid.NewGuid():N}");

        try
        {
            // No action may ever be directed at the developer's source repo.
            // The test owns exclusively these randomly named temp directories.
            Directory.CreateDirectory(unsafeRoot);
            string sentinel = Path.Combine(unsafeRoot, "keep.txt");
            File.WriteAllText(sentinel, "do not overwrite");
            ExpectRejected(() => SegmentGitWorkspace.Initialize(unsafeRoot),
                "initialization must reject existing nonempty folders");
            Equal("do not overwrite", File.ReadAllText(sentinel), "existing content untouched");

            SegmentGitWorkspace workspace = SegmentGitWorkspace.Initialize(root);
            Check(Directory.Exists(Path.Combine(root, ".git")),
                "workspace must have its own independent Git repository");
            Equal("main", workspace.CurrentBranch, "initial branch");
            Equal(Path.GetFullPath(root), Path.GetFullPath(workspace.Directory),
                "workspace root directory");

            SegmentGitSnapshot initial = Fixture();
            SegmentTimePlanner.Validate(initial.Timeline);
            workspace.Save(initial);
            Check(File.Exists(Path.Combine(root, "timeline.json")),
                "time assignments must be stored separately from segment content");
            Check(File.Exists(Path.Combine(root, "segments", "A.json")),
                "Segment A must be a separate versioned file");
            Check(File.Exists(Path.Combine(root, "segments", "B.json")),
                "Segment B must be a separate versioned file");

            byte[] timelineBytes = File.ReadAllBytes(Path.Combine(root, "timeline.json"));
            byte[] aBytes = File.ReadAllBytes(Path.Combine(root, "segments", "A.json"));
            byte[] bBytes = File.ReadAllBytes(Path.Combine(root, "segments", "B.json"));
            workspace.Save(initial);
            Sequence(timelineBytes, File.ReadAllBytes(Path.Combine(root, "timeline.json")),
                "identical snapshots must serialize deterministically (timeline)");
            Sequence(aBytes, File.ReadAllBytes(Path.Combine(root, "segments", "A.json")),
                "identical snapshots must serialize deterministically (A)");
            Sequence(bBytes, File.ReadAllBytes(Path.Combine(root, "segments", "B.json")),
                "identical snapshots must serialize deterministically (B)");

            CheckSnapshot(workspace.Load(), "A-r1", "B-r1", 0,
                "initial snapshot roundtrip");
            LevelSegment contentB = workspace.Load().Segments["B"];
            Check(contentB.Actions[0].SourceProperties?["unknown"]?["nested"]?.ToString() == "yes",
                "unknown nested event JSON must survive persistence");
            Check(contentB.Decorations[0].Properties["extra"]?["mystery"]?.ToString() == "42",
                "unknown decoration JSON must survive persistence");
            Equal(999d, contentB.DestinationAngles[1], "midspin value survives persistence");
            contentB.Actions[0].SourceProperties!["tag"] = "mutated-reader";
            Equal("fx", workspace.Load().Segments["B"].Actions[0].SourceProperties?["tag"]?.ToString(),
                "loading a snapshot must not leak mutable JSON aliases");

            // The commit must stage only collaboration snapshot files, not
            // arbitrary untracked files in the temp workspace.
            File.WriteAllText(Path.Combine(root, "local-secret.txt"), "never commit this");
            string baseCommit = workspace.Commit("base segments");
            Check(IsSha(baseCommit), "commit returns a full Git SHA");
            SegmentGitWorkspace reopened = SegmentGitWorkspace.Open(root);
            Equal("main", reopened.CurrentBranch, "open existing repository on main");
            CheckSnapshot(reopened.Load(), "A-r1", "B-r1", 0,
                "snapshot survives reopening the workspace");
            string trackedFiles = RunGit(root, "ls-tree", "-r", "--name-only", "HEAD");
            Check(trackedFiles.Contains("timeline.json", StringComparison.Ordinal),
                "timeline tracked by Git");
            Check(trackedFiles.Contains("segments/A.json", StringComparison.Ordinal),
                "Segment A tracked by Git");
            Check(trackedFiles.Contains("segments/B.json", StringComparison.Ordinal),
                "Segment B tracked by Git");
            Check(!trackedFiles.Contains("local-secret.txt", StringComparison.Ordinal),
                "unrelated user files must never be automatically committed");

            workspace.CreateBranch("work/A");
            workspace.CreateBranch("work/B");
            workspace.Checkout("work/A");
            Equal("work/A", workspace.CurrentBranch, "checkout work/A");
            SegmentGitSnapshot left = workspace.Load();
            workspace.Save(Replace(left, "A", "A-r2", revisedFloor: 45));
            string leftCommit = workspace.Commit("edit A");
            Check(IsSha(leftCommit) && leftCommit != baseCommit, "A has a new commit");
            CheckSnapshot(workspace.Load(), "A-r2", "B-r1", 45, "A working tree");

            workspace.Checkout("work/B");
            Equal("work/B", workspace.CurrentBranch, "checkout work/B");
            CheckSnapshot(workspace.Load(), "A-r1", "B-r1", 0,
                "branch B starts from base, no A changes leaked");
            SegmentGitSnapshot right = workspace.Load();
            workspace.Save(Replace(right, "B", "B-r2", revisedFloor: 270));
            string rightCommit = workspace.Commit("edit B");
            Check(IsSha(rightCommit) && rightCommit != leftCommit,
                "B has an independent new commit");
            CheckSnapshot(workspace.Load(), "A-r1", "B-r2", 0, "B working tree");

            // Git read operations must not move HEAD, checkout or rewrite the
            // current working tree. This is the seam for semantic 3-way merge.
            SegmentGitSnapshot baseView = workspace.ReadAt(baseCommit);
            SegmentGitSnapshot leftView = workspace.ReadAt("work/A");
            SegmentGitSnapshot rightView = workspace.ReadAt("work/B");
            CheckSnapshot(baseView, "A-r1", "B-r1", 0, "immutable base commit");
            CheckSnapshot(leftView, "A-r2", "B-r1", 45, "immutable A branch");
            CheckSnapshot(rightView, "A-r1", "B-r2", 0, "immutable B branch");
            Equal("work/B", workspace.CurrentBranch, "ReadAt does not checkout");
            CheckSnapshot(workspace.Load(), "A-r1", "B-r2", 0,
                "ReadAt leaves B worktree untouched");

            SegmentTimeMergeResult semantics = SegmentTimeThreeWayMerger.Merge(
                baseView.Timeline, leftView.Timeline, rightView.Timeline);
            Check(semantics.Success && semantics.Timeline is not null,
                "saved Git snapshots must feed the existing semantic 3-way merger");
            Equal("A-r2", semantics.Timeline!.Partitions.Single(p => p.SegmentId == "A").ContentRevisionId,
                "semantic merge adopts A's independent revision");
            Equal("B-r2", semantics.Timeline!.Partitions.Single(p => p.SegmentId == "B").ContentRevisionId,
                "semantic merge adopts B's independent revision");

            // Dirty worktrees must never be overwritten by branch switches.
            SegmentGitSnapshot pending = workspace.Load();
            workspace.Save(Replace(pending, "B", "B-uncommitted", revisedFloor: 180));
            ExpectRejected(() => workspace.Checkout("work/A"),
                "checkout must reject uncommitted edits; no implicit reset, clean or stash");
            Equal("work/B", workspace.CurrentBranch,
                "rejected checkout leaves current branch unchanged");
            Equal("B-uncommitted",
                workspace.Load().Timeline.Partitions.Single(p => p.SegmentId == "B").ContentRevisionId,
                "rejected checkout preserves pending content");
            CheckSnapshot(workspace.ReadAt("work/B"), "A-r1", "B-r2", 0,
                "pending work did not alter committed history");

            // A path-unsafe ID must be rejected before it can write outside
            // the dedicated workspace or overwrite Git's internal files.
            SegmentGitSnapshot unsafeSnapshot = Fixture();
            var invalidSegments = unsafeSnapshot.Segments.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            invalidSegments["../outside"] = unsafeSnapshot.Segments["A"];
            ExpectRejected(() => workspace.Save(unsafeSnapshot with { Segments = invalidSegments }),
                "path traversal in SegmentId");
        }
        finally
        {
            // These are only this test's unique temp repositories, never the
            // user's source checkout or their personal working directories.
            // Git may mark .git/objects files read-only on Windows.
            // Cleanup must not mask the actual assertion failure, if any.
            TryCleanupTemporaryRepository(root);
            TryCleanupTemporaryRepository(unsafeRoot);
        }
    }

    private static void TryCleanupTemporaryRepository(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            // Fail closed on links/junctions; this test must never traverse
            // outside its own temporary directory during cleanup.
            var directories = new Stack<string>();
            directories.Push(path);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                FileAttributes dirAttributes = File.GetAttributes(directory);
                if ((dirAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to follow a linked test directory.");

                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing to follow a linked test entry.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                    }
                    else if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                if ((dirAttributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(directory, dirAttributes & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // A cleanup failure is diagnostic only. In particular, it must
            // never overwrite the original exception from the regression.
            Console.Error.WriteLine(
                $"WARNING: Could not clean up isolated Git test directory '{path}': {ex.Message}");
        }
    }

    private static SegmentGitSnapshot Fixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(5);
        level.Angles = [0d, 45d, 999d, 90d];
        level.RebuildGeometry();
        LevelSegment[] segments = LevelSegmentPartitioner.Split(level, 2).ToArray();
        LevelSegment b = segments[1] with
        {
            Actions =
            [
                new LevelAction(0, "MoveDecorations", true, null, null, null, null)
                {
                    SourceIndex = 3,
                    Duration = 2.0,
                    SourceProperties = new JsonObject
                    {
                        ["tag"] = "fx",
                        ["unknown"] = new JsonObject { ["nested"] = "yes" }
                    }
                }
            ],
            Decorations =
            [
                new LevelDecoration(0, "AddDecoration")
                {
                    SourceIndex = 5,
                    Properties = new JsonObject
                    {
                        ["decorationImage"] = "sprite.png",
                        ["extra"] = new JsonObject { ["mystery"] = 42 }
                    }
                }
            ]
        };
        return new SegmentGitSnapshot(
            new SegmentTimeTimeline(SampleRate,
            [
                new("A", S(0), S(10), "A-r1", S(0), "user-A", SegmentPartitionStatus.Active),
                new("B", S(10), S(20), "B-r1", S(10), "user-B", SegmentPartitionStatus.Active)
            ]),
            new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
            {
                ["A"] = segments[0],
                ["B"] = b
            });
    }

    private static SegmentGitSnapshot Replace(
        SegmentGitSnapshot snapshot, string id, string revision, double revisedFloor)
    {
        var revisedPartitions = snapshot.Timeline.Partitions
            .Select(part => part.SegmentId == id
                ? part with { ContentRevisionId = revision }
                : part)
            .ToArray();
        var revisedSegments = snapshot.Segments.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        LevelSegment existing = revisedSegments[id];
        double[] angles = existing.DestinationAngles.ToArray();
        angles[0] = revisedFloor;
        revisedSegments[id] = existing with { DestinationAngles = angles };
        return new SegmentGitSnapshot(
            snapshot.Timeline with { Partitions = revisedPartitions },
            revisedSegments);
    }

    private static void CheckSnapshot(
        SegmentGitSnapshot snapshot, string aRevision, string bRevision, double firstAAngle,
        string description)
    {
        SegmentTimePlanner.Validate(snapshot.Timeline);
        Equal(aRevision,
            snapshot.Timeline.Partitions.Single(p => p.SegmentId == "A").ContentRevisionId,
            description + " A revision");
        Equal(bRevision,
            snapshot.Timeline.Partitions.Single(p => p.SegmentId == "B").ContentRevisionId,
            description + " B revision");
        Equal(firstAAngle, snapshot.Segments["A"].DestinationAngles[0],
            description + " A first angle");
        Check(snapshot.Segments.ContainsKey("B") && snapshot.Segments["B"].Actions.Length == 1 &&
              snapshot.Segments["B"].Decorations.Length == 1,
            description + " B event and decoration");
    }

    private static string RunGit(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Cannot launch Git subprocess.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Git {string.Join(" ", args)} failed: {error}");
        return output;
    }

    private static bool IsSha(string sha) =>
        sha.Length == 40 && sha.All(c => char.IsAsciiHexDigit(c));

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string description)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
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
        throw new InvalidOperationException(
            $"Unsafe workspace operation was accepted: {description}");
    }
}
