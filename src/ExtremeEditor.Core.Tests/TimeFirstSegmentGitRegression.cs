using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TimeFirstSegmentGitRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-time-git-{Guid.NewGuid():N}");
        string aPath = Path.Combine(root, "A");
        string bPath = Path.Combine(root, "B");
        string remote = Path.Combine(root, "shared.git");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "--bare", "--initial-branch=main", remote);
            TimeFirstSegmentGitWorkspace a =
                TimeFirstSegmentGitWorkspace.Initialize(aPath);
            a.Save(Fixture());
            a.Commit("base");
            TimeFirstSegmentGitRemoteSync.ConnectLocalBare(a, remote);
            var monitoredRemote = new CountingRemote();
            TimeFirstSegmentGitRemoteSync.Push(a, remote: monitoredRemote);
            Check(monitoredRemote.PushCount == 1, "injected remote handles Push");

            TimeFirstSegmentGitWorkspace b =
                TimeFirstSegmentGitRemoteSync.CloneLocalBare(remote, bPath);
            string common = a.HeadCommit;

            a.Save(Edit(a.Load(), "A", "alice", "A-1", "a-tile", Rate * 2L, 0));
            string aCommit = a.Commit("Alice edits A");
            TimeFirstSegmentGitRemoteSync.Push(a);

            b.Save(Edit(b.Load(), "B", "bob", "B-1", "b-tile", Rate * 12L, 90));
            string bCommit = b.Commit("Bob edits B");
            Check(bCommit != common && aCommit != common, "independent local histories");
            TimeFirstSegmentPullResult merged = TimeFirstSegmentGitRemoteSync.Pull(b, remote: monitoredRemote);
            Check(monitoredRemote.FetchCount == 1,
                "semantic Pull fetches through the injected remote");
            Check(merged.Success, "different Segment edits merge semantically");
            Check(b.Load().Authored?.Keys.Order().SequenceEqual(["A", "B"]) == true,
                "both authors' real tile edits survive");
            string[] parents = RunGit(bPath, "show", "-s", "--format=%P", "HEAD")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Check(parents.Length == 2, "divergent Pull creates two-parent history");
            TimeFirstSegmentGitRemoteSync.Push(b);
            Check(TimeFirstSegmentGitRemoteSync.Pull(a).Success,
                "first client receives merged history");

            a.Save(Edit(a.Load(), "A", "alice", "A-2", "a-second", Rate * 3L, 180));
            a.Commit("Alice edits A again");
            TimeFirstSegmentGitRemoteSync.Push(a);

            string bBeforeConflict = b.HeadCommit;
            b.Save(Edit(b.Load(), "A", "alice", "A-alt", "a-alt", Rate * 4L, 270));
            b.Commit("Concurrent edit to A");
            string bLocal = b.HeadCommit;
            TimeFirstSegmentPullResult conflict = TimeFirstSegmentGitRemoteSync.Pull(b);
            Check(!conflict.Success && conflict.ContentConflictSegmentIds.SequenceEqual(["A"]),
                "same Segment edit is explicit conflict");
            Check(b.HeadCommit == bLocal && b.HeadCommit != bBeforeConflict,
                "conflict preserves local HEAD and history");
            Check(b.Load().Authored?["A"].RevisionId == "A-alt",
                "conflict preserves local authored payload");

            TimeFirstSegmentSnapshot dirty = b.Load();
            b.Save(dirty with { Audio = dirty.Audio with { FileName = "dirty.ogg" } });
            ExpectRejected(() => TimeFirstSegmentGitRemoteSync.Pull(b),
                "uncommitted owned data blocks Pull");
            Check(b.Load().Audio.FileName == "dirty.ogg",
                "dirty data is not discarded");
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    private sealed class CountingRemote : ITimeFirstSegmentRemote
    {
        public int FetchCount { get; private set; }
        public int PushCount { get; private set; }

        public string Fetch(TimeFirstSegmentGitWorkspace workspace, string branchName)
        {
            FetchCount++;
            return LocalBareTimeFirstSegmentRemote.Instance.Fetch(workspace, branchName);
        }

        public string Push(TimeFirstSegmentGitWorkspace workspace, string branchName)
        {
            PushCount++;
            return LocalBareTimeFirstSegmentRemote.Instance.Push(workspace, branchName);
        }
    }

    private static TimeFirstSegmentSnapshot Edit(
        TimeFirstSegmentSnapshot snapshot, string segmentId, string owner,
        string revision, string tileId, long sample, double angle)
    {
        var editor = TimeFirstSegmentEditorSession.Open(snapshot, segmentId, owner);
        editor.PlaceTile(tileId, sample, angle);
        return editor.CreateRevision(snapshot, revision);
    }

    private static TimeFirstSegmentSnapshot Fixture()
    {
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "A", 0, Rate * 10L, "alice");
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "B", Rate * 10L, Rate * 20L, "bob");
        var content = timeline.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty, StringComparer.Ordinal);
        var drafts = timeline.Partitions.ToDictionary(
            p => p.SegmentId,
            p => SegmentTimeDraftEditor.Open(timeline, content, p.SegmentId, p.OwnerId!),
            StringComparer.Ordinal);
        return new TimeFirstSegmentSnapshot(
            new TimeFirstAudioIdentity("song.ogg", new string('c', 64), Rate, Rate * 20L),
            [new MusicTimingPoint(0, 120m)], timeline, content, drafts);
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
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git fixture failed: " + error);
        return output.Trim();
    }

    private static void CleanupRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked fixture entry.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe Git operation accepted: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
