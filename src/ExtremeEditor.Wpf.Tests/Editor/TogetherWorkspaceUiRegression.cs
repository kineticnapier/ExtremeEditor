using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Headless/STA contract for the WPF Together UI boundary. Real EditorSession
/// operations and real isolated Git repositories are used; NO GUI automation,
/// simulated keypresses, MainWindow startup or network remotes are involved.
/// </summary>
internal static class TogetherWorkspaceUiRegression
{
    private const int SampleRate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-together-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExerciseAuthoringAndUiContract(root);
        }
        finally
        {
            CleanTemporaryRoot(root);
        }
    }

    private static void ExerciseAuthoringAndUiContract(string root)
    {
        string sourcePath = Path.Combine(root, "original.adofai");
        string output = Path.Combine(root, "completed.adofai");
        string bare = Path.Combine(root, "shared.git");
        string aPath = Path.Combine(root, "author-A");
        string bPath = Path.Combine(root, "author-B");
        File.WriteAllText(sourcePath, """
        {
          "angleData": [0, 45, 999, 90, 270, 15],
          "unknownRoot": {"kept": 42},
          "settings": {
            "bpm": 120, "songFilename": "audio.ogg", "offset": 2000,
            "pitch": 100, "countdownTicks": 0, "separateCountdownTime": false
          },
          "actions": [
            {"floor": 1, "eventType": "SetHitsound", "hitsound": "Kick",
             "futureProperty": {"preserved": true}},
            {"floor": 2, "eventType": "Twirl"},
            {"floor": 4, "eventType": "CustomBackground", "active": false,
             "custom": {"unchanged": true}}
          ],
          "decorations": [
            {"floor": 4, "eventType": "AddDecoration",
             "decorationImage": "sprite.png", "extra": {"future": 7}},
            {"eventType": "AddText", "text": "GLOBAL"}
          ]
        }
        """);
        byte[] originalBytes = File.ReadAllBytes(sourcePath);
        LevelDocument source = AdoFaiLoader.Load(sourcePath).Document;
        LevelSegment[] segments = LevelSegmentPartitioner.Split(source, 3).ToArray();
        SegmentGitSnapshot seeded = new(
            Assignments(source),
            new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
            {
                ["A"] = segments[0],
                ["B"] = segments[1]
            });

        var a = new TogetherWorkspaceController();
        a.Create(aPath, source, seeded, ownerId: "author-A");
        Check(a.Display.WorkspaceDirectory == Path.GetFullPath(aPath),
            "Create must expose actual initialized local collaboration path");
        Check(a.Display.Segments.Count == 2, "WPF presentation lists both segments");
        Check(a.Display.Segments.Single(p => p.SegmentId == "A").CanEdit,
            "active owner A can edit A");
        Check(!a.Display.Segments.Single(p => p.SegmentId == "B").CanEdit,
            "other owner's B is visible but not editable");
        Check(a.Display.Segments.Single(p => p.SegmentId == "B").StartFloor == 3,
            "floor range is visible independently of audio samples");
        Check(a.Display.Segments.All(p => p.EndSampleExclusive > p.StartSample),
            "ownership sample ranges are visible");

        ExpectRejected(() => a.SelectSegment("B"),
            "author A cannot select B as writable");
        a.SelectSegment("A");
        Equal("A", a.Display.SelectedSegmentId, "explicit selected Segment A");
        Equal(1, a.Display.Segments.Count(p => p.IsSelected),
            "only one active segment is selected");
        ExpectRejected(() => a.Commit("no edits"),
            "Commit cannot invent author work without a staged edit");

        Directory.CreateDirectory(bare);
        RunGit(root, "init", "--bare", "--initial-branch=main", bare);
        a.ConnectLocalBare(bare);
        a.Push();
        SegmentGitRemoteSync.CloneLocalBare(bare, bPath);

        var b = new TogetherWorkspaceController();
        b.Open(bPath, source, ownerId: "author-B");
        b.SelectSegment("B");
        Check(!b.Display.Segments.Single(p => p.SegmentId == "A").CanEdit,
            "author B cannot edit A");
        Check(b.Display.Segments.Single(p => p.SegmentId == "B").CanEdit,
            "author B can edit B");

        // Use actual editor command/undo model instead of inventing Core
        // segments from UI input.
        EditorSession aEditor = new(a.GetEditorDocument());
        LevelAction originalHit = aEditor.GetActionsAtFloor(1).Single(
            action => action.EventType == "SetHitsound");
        aEditor.ReplaceAction(originalHit, originalHit with { HitSound = "Snare" });
        Check(aEditor.IsDirty && aEditor.CanUndo,
            "the regular WPF editor owns its undo history before staging");
        a.StageSelected(aEditor);
        Check(a.Display.HasStagedChanges,
            "status indicates staged changes are waiting for explicit Commit");
        string aCommit = a.Commit("A changes hitsound");
        Equal(aCommit, a.Display.CurrentCommit, "commit SHA shown in UI state");
        Check(!a.Display.HasStagedChanges,
            "completed commit clears only the collaboration staging state");
        a.Push();
        Check(aEditor.CanUndo, "Together commit must not clear the editor's undo stack");

        // An attempt to stage edits to a non-owned floor must be rejected
        // WITHOUT modifying the persisted Segment JSON.
        EditorSession foreignEdit = new(b.GetEditorDocument());
        foreignEdit.Rotate([1], 90.0);
        byte[] beforeB = File.ReadAllBytes(Path.Combine(bPath, "segments", "B.json"));
        ExpectRejected(() => b.StageSelected(foreignEdit),
            "B cannot stage an angle change on A's floor");
        Sequence(beforeB,
            File.ReadAllBytes(Path.Combine(bPath, "segments", "B.json")),
            "rejected cross-ownership edit preserves persisted Segment B");
        Check(foreignEdit.IsDirty && foreignEdit.CanUndo,
            "rejected edit remains in the WPF undo buffer");

        EditorSession bEditor = new(b.GetEditorDocument());
        bEditor.AddAction(4, "SetFloorIcon");
        Check(bEditor.IsDirty, "B typed WPF event is an unsaved edit");
        b.StageSelected(bEditor);
        string bCommit = b.Commit("B adds floor icon event");
        Check(aCommit != bCommit, "independent client edit histories diverge");
        ExpectRejected(() => b.Push(),
            "non-fast-forward publish is rejected, never forced");

        // Explicit Pull is prohibited if a separate editor buffer contains
        // unsaved work, even if the Git collaboration files are clean.
        EditorSession dirtyBuffer = new(b.GetEditorDocument());
        dirtyBuffer.AddAction(5, "Checkpoint");
        Check(dirtyBuffer.IsDirty, "dirty WPF buffer fixture");
        byte[] bTimelineBefore = File.ReadAllBytes(Path.Combine(bPath, "timeline.json"));
        ExpectRejected(() => b.Pull(dirtyBuffer),
            "semantic Pull may not discard an unsaved editor buffer");
        Sequence(bTimelineBefore, File.ReadAllBytes(Path.Combine(bPath, "timeline.json")),
            "dirty Pull rejection protects local timeline file");
        Check(dirtyBuffer.IsDirty && dirtyBuffer.CanUndo,
            "dirty Pull rejection preserves unsaved authoring history");

        Equal(aCommit, b.Fetch(), "Fetch observes published A revision");
        Equal(bCommit, b.Display.CurrentCommit,
            "Fetch updates remote knowledge but does not overwrite local HEAD");
        SegmentGitPullResult merged = b.Pull(new EditorSession(b.GetEditorDocument()));
        Check(merged.Success && merged.LocalCommit is not null,
            "explicit semantic Pull merges non-overlapping author edits");
        Check(b.Display.ContentConflictSegmentIds.Count == 0 &&
              b.Display.TimelineConflicts.Count == 0,
            "successful Pull clears displayed conflict state");
        string mergedSha = merged.LocalCommit!;
        Equal(mergedSha, b.Push(), "explicit Push publishes merged content");
        a.Fetch();
        SegmentGitPullResult aPulled =
            a.Pull(new EditorSession(a.GetEditorDocument()));
        Check(aPulled.Success, "client A catches up with remote");
        Equal(mergedSha, a.Display.CurrentCommit,
            "A displays same pinned merged revision");

        SegmentGitAdoFaiPublication published = a.Export(output);
        Equal(mergedSha, published.SourceCommit,
            "UI export must use the published merged Git SHA");
        Check(File.Exists(output), "validated file was created");
        LevelDocument reloaded = AdoFaiLoader.Load(output).Document;
        Equal("Snare", reloaded.ActionStore.Actions.Single(
            action => action.EventType == "SetHitsound").HitSound,
            "A's actual WPF event edit reaches the exported chart");
        Check(reloaded.ActionStore.Actions.Any(action =>
            action.Floor == 4 && action.EventType == "SetFloorIcon"),
            "B's actual WPF editor action reaches the exported chart");
        JsonObject raw = JsonNode.Parse(File.ReadAllText(output))!.AsObject();
        Equal("42", raw["unknownRoot"]?["kept"]?.ToString(),
            "unknown project metadata survives UI-initiated export");
        Equal("true", raw["actions"]![0]?["futureProperty"]?["preserved"]?.ToString()
            .ToLowerInvariant(), "unknown action metadata survives");
        Sequence(originalBytes, File.ReadAllBytes(sourcePath),
            "the source .adofai file remains unchanged");
        byte[] completedBytes = File.ReadAllBytes(output);

        // Explicit conflicts must become visible to the presenter. They must
        // never turn into a false "synced" status or replace prior output.
        EditorSession otherEditor = new(b.GetEditorDocument());
        LevelAction otherHit = otherEditor.GetActionsAtFloor(1).Single(
            action => action.EventType == "SetHitsound");
        // B is not the owner of A; the controller must reject selecting A.
        ExpectRejected(() => b.SelectSegment("A"),
            "editing another author's floor requires ownership reassignment");
        // After A starts a new unpublished commit, an earlier published
        // revision may no longer be claimed as the current remote HEAD.
        EditorSession nextEdit = new(a.GetEditorDocument());
        LevelAction hit = nextEdit.GetActionsAtFloor(1).Single(
            action => action.EventType == "SetHitsound");
        nextEdit.ReplaceAction(hit, hit with { HitSound = "Clap" });
        a.SelectSegment("A");
        a.StageSelected(nextEdit);
        a.Commit("A unpublished next edit");
        ExpectRejected(() => a.Export(output, overwriteExisting: true),
            "unpublished editor commit cannot silently replace exported chart");
        Sequence(completedBytes, File.ReadAllBytes(output),
            "rejected publication preserves existing completed chart");

        // No GUI input automation: only type/field metadata is inspected.
        // GREEN must add a real discoverable menu entry and WPF panel.
        RequireWpfUiSurface();
    }

    private static SegmentTimeTimeline Assignments(LevelDocument source)
    {
        TimingMap timing = TimingMapBuilder.Build(source);
        var partitions = new[] { ("A", 0, 3, "author-A"), ("B", 3, source.FloorCount, "author-B") }
            .Select(range =>
            {
                double start = PlaybackClock.ChartToAudioTime(
                    source, timing.GetEntryTime(range.Item2));
                double end = PlaybackClock.ChartToAudioTime(source,
                    range.Item3 == source.FloorCount
                        ? timing.Duration : timing.GetEntryTime(range.Item3));
                long startSample = ToSample(start);
                long endSample = ToSample(end);
                return new SegmentTimePartition(
                    range.Item1, startSample, endSample, range.Item1 + "-r1",
                    startSample, range.Item4, SegmentPartitionStatus.Active);
            }).ToArray();
        return new SegmentTimeTimeline(SampleRate, partitions);
    }

    private static long ToSample(double seconds) =>
        checked((long)Math.Round(seconds * SampleRate,
            MidpointRounding.AwayFromZero));

    private static void RequireWpfUiSurface()
    {
        const BindingFlags fields = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        Check(typeof(MainWindow).GetField("TogetherMenuItem", fields) is not null,
            "MainWindow must expose a Together menu item");
        Check(typeof(MainWindow).GetMethod("OpenTogetherClick", fields) is not null,
            "MainWindow menu must have an actual Together window handler");

        Type dialog = typeof(MainWindow).Assembly.GetType(
            "ExtremeEditor.Wpf.TogetherWorkspaceWindow")
            ?? throw new InvalidOperationException(
                "GREEN must include an actual WPF Together workspace panel");
        foreach (string field in new[]
                 {
                     "CreateWorkspaceButton", "OpenWorkspaceButton",
                     "SegmentList", "SaveSegmentButton", "CommitButton",
                     "FetchButton", "PullButton", "ExportButton",
                     "TogetherStatusText"
                 })
        {
            Check(dialog.GetField(field, fields) is not null,
                "WPF Together panel is missing interactive surface " + field);
        }
    }

    private static string RunGit(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process p = Process.Start(info)
            ?? throw new InvalidOperationException("Unable to run local test Git process");
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15_000))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Isolated test Git process timed out");
        }
        string errors = stderr.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("git failure: " + errors);
        return stdout;
    }

    private static void CleanTemporaryRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                FileAttributes attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked cleanup directory");
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    FileAttributes entryAttrs = File.GetAttributes(entry);
                    if ((entryAttrs & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing linked cleanup entry");
                    if ((entryAttrs & FileAttributes.Directory) != 0) pending.Push(entry);
                    else if ((entryAttrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, entryAttrs & ~FileAttributes.ReadOnly);
                }
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                "WARNING: E2E UI test temp cleanup failed: " + ex.Message);
        }
    }

    private static void ExpectRejected(Action action, string description)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsafe workflow accepted: " + description);
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Sequence mismatch: " + label);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                label + ": expected " + expected + ", got " + actual);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
