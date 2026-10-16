using System.Diagnostics;
using System.Security.Cryptography;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Core end-to-end rehearsal for the Phase H manual two-client workflow.
/// This does not replace Windows WPF or in-game verification.
/// </summary>
internal static class TimeFirstTogetherEndToEndRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ee-time-first-h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string bare = Path.Combine(root, "remote.git");
            RunGit(root, "init", "--bare", "--initial-branch=main", bare);
            byte[] audio = [42, 13, 90, 26, 88];
            File.WriteAllBytes(Path.Combine(root, "song.ogg"), audio);
            string hash = Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant();

            SegmentTimeTimeline timeline = new(Rate, []);
            timeline = SegmentTimePlanner.CreateEmpty(timeline, "A", 0, Rate, "alice");
            timeline = SegmentTimePlanner.CreateEmpty(timeline, "B", Rate, Rate * 2L, "bob");
            Dictionary<string, SegmentContentEntry> content = timeline.Partitions.ToDictionary(
                p => p.SegmentId, SegmentContentPlanner.CreateEmpty,
                StringComparer.Ordinal);
            Dictionary<string, SegmentTimeDraft> drafts = timeline.Partitions.ToDictionary(
                p => p.SegmentId, p => SegmentTimeDraftEditor.Open(timeline,
                    content, p.SegmentId, p.OwnerId!), StringComparer.Ordinal);
            TimeFirstSegmentSnapshot initial = new(
                new TimeFirstAudioIdentity("song.ogg", hash, Rate, Rate * 2L),
                [new MusicTimingPoint(0, 120m)], timeline, content, drafts);

            TimeFirstSegmentGitWorkspace a = TimeFirstSegmentGitWorkspace.Initialize(
                Path.Combine(root, "alice"));
            a.Save(initial);
            a.Commit("create two empty allocations");
            TimeFirstSegmentGitRemoteSync.ConnectLocalBare(a, bare);
            TimeFirstSegmentGitRemoteSync.Push(a);
            TimeFirstSegmentGitWorkspace b = TimeFirstSegmentGitRemoteSync.CloneLocalBare(
                bare, Path.Combine(root, "bob"));

            var editorA = TimeFirstSegmentEditorSession.Open(a.Load(), "A", "alice");
            editorA.SetEntryAngle(270);
            editorA.PlaceTile("first", Rate / 2L, 0);
            a.Save(editorA.CreateRevision(a.Load(), "A-complete"));
            a.Commit("alice authors first half");
            TimeFirstSegmentGitRemoteSync.Push(a);

            var editorB = TimeFirstSegmentEditorSession.Open(b.Load(), "B", "bob");
            editorB.SetEntryAngle(270);
            editorB.PlaceTile("second", Rate, 0);
            editorB.ReplaceActions("second", [new LevelAction(1, "SetHitsound", true,
                null, null, null, null) { HitSound = "Kick" }]);
            b.Save(editorB.CreateRevision(b.Load(), "B-complete"));
            b.Commit("bob authors second half");
            TimeFirstSegmentPullResult merged = TimeFirstSegmentGitRemoteSync.Pull(b);
            Check(merged.Success, "non-overlapping authored segments merge");
            Check(b.Load().Authored?.Count == 2, "both authored revisions survive");
            Check(RunGit(b.Directory, "show", "-s", "--format=%P", "HEAD")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2,
                "merge retains two-parent ancestry");
            TimeFirstSegmentGitRemoteSync.Push(b);
            Check(TimeFirstSegmentGitRemoteSync.Pull(a).Success,
                "alice can receive the authored merge");
            Check(a.Load().Authored?.Count == 2, "the received chart has both segments");

            var playback = new TimeFirstAdoFaiSettings(120);
            TimeFirstAdoFaiCompositionResult composition =
                TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(a.Load(), playback);
            Check(composition.Accepted, "two-client chart passes conservative publication");
            string output = Path.Combine(root, "finished.adofai");
            TimeFirstAdoFaiExportResult finished =
                TimeFirstSegmentAdoFaiExporter.ExportValidated(a, playback, output);
            Check(finished.FloorCount == 3, "three floors including start");
            LevelDocument readback = AdoFaiLoader.Load(output).Document;
            Check(readback.FloorCount == 3 && readback.ActionStore.Actions.Any(action =>
                action.Floor == 2 && action.HitSound == "Kick"),
                "merged authored event survives actual chart roundtrip");
        }
        finally
        {
            // Temporary test root is self-contained. Refuse linked descendants.
            if (Directory.Exists(root))
            {
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count != 0)
                {
                    string dir = pending.Pop();
                    foreach (string item in Directory.EnumerateFileSystemEntries(dir))
                    {
                        FileAttributes attributes = File.GetAttributes(item);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Refusing a linked Git fixture entry.");
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                        else if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(item, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string RunGit(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git fixture failed: " + error);
        return output.Trim();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Phase H E2E: " + message);
    }
}
