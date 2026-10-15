using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Uses real isolated Git repositories, an actual bare origin and the Core
/// .adofai reader. The developer's working Git repository is never used.
/// </summary>
internal static class SegmentTogetherE2ERegression
{
    private const int SampleRate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-together-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TwoAuthorsMergePublishAndReopen(root);
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    private static void TwoAuthorsMergePublishAndReopen(string root)
    {
        string sourcePath = Path.Combine(root, "original.adofai");
        string outputPath = Path.Combine(root, "collaborative.adofai");
        string bare = Path.Combine(root, "shared.git");
        string aPath = Path.Combine(root, "client-A");
        string bPath = Path.Combine(root, "client-B");

        File.WriteAllText(sourcePath, """
        {
          "angleData": [0, 45, 999, 90, 270, 15],
          "customRoot": {"future": ["preserve", {"v": 7}]},
          "settings": {
            "bpm": 120, "songFilename": "song.ogg", "offset": 2000,
            "pitch": 100, "countdownTicks": 0, "separateCountdownTime": false,
            "unknownSetting": {"keep": 123}
          },
          "actions": [
            {"floor": 1, "eventType": "SetHitsound", "hitsound": "Kick",
             "futureOnKnownEvent": {"unchanged": true}},
            {"floor": 2, "eventType": "Twirl"},
            {"floor": 4, "eventType": "CustomBackground", "active": false,
             "custom": {"nested": {"revision": "base"}}}
          ],
          "decorations": [
            {"floor": 4, "eventType": "AddDecoration", "decorationImage": "fx.png",
             "extra": {"revision": "base"}},
            {"eventType": "AddText", "text": "project-wide",
             "extra": {"keep": true}}
          ]
        }
        """);

        byte[] sourceBefore = File.ReadAllBytes(sourcePath);
        LevelDocument original = AdoFaiLoader.Load(sourcePath).Document;
        LevelSegment[] sections = LevelSegmentPartitioner.Split(original, 3).ToArray();
        Check(sections.Length == 2, "expected two independent author regions");

        LevelDocument unedited = LevelSegmentPartitioner.Merge(original, sections);
        SegmentTimeTimeline allocations = Allocations(unedited);
        var content = new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
        {
            ["A"] = sections[0],
            ["B"] = sections[1]
        };

        Directory.CreateDirectory(bare);
        RunGit(root, "init", "--bare", "--initial-branch=main", bare);

        SegmentGitWorkspace a = SegmentGitWorkspace.Initialize(aPath);
        a.Save(new SegmentGitSnapshot(allocations, content));
        string rootSha = a.Commit("published original chart");
        SegmentGitRemoteSync.ConnectLocalBare(a, bare);
        Equal(rootSha, SegmentGitRemoteSync.Push(a), "initial publish");
        SegmentGitWorkspace b = SegmentGitRemoteSync.CloneLocalBare(bare, bPath);
        Equal(rootSha, GitHead(bPath), "B clones original history");

        EditSegment(a, "A", part =>
        {
            LevelAction[] actions = part.Actions.Select(action =>
                action.EventType == "SetHitsound"
                    ? action with { HitSound = "Snare" }
                    : action).ToArray();
            return part with { Actions = actions };
        }, "A-r2");
        string aSha = a.Commit("A edits SetHitsound in region A");
        SegmentGitRemoteSync.Push(a);

        EditSegment(b, "B", part =>
        {
            LevelAction[] actions = part.Actions.Select(action =>
            {
                if (action.EventType != "CustomBackground")
                    return action;
                JsonObject raw = (JsonObject)action.SourceProperties!.DeepClone();
                raw["custom"]!["nested"]!["revision"] = "B-edited";
                JsonObject overrides = (JsonObject)action.PropertyOverrides!.DeepClone();
                overrides["custom"]!["nested"]!["revision"] = "B-edited";
                return action with
                {
                    SourceProperties = raw,
                    PropertyOverrides = overrides
                };
            }).ToArray();
            LevelDecoration[] decorations = part.Decorations.Select(decoration =>
            {
                JsonObject raw = (JsonObject)decoration.Properties.DeepClone();
                raw["extra"]!["revision"] = "B-edited";
                return decoration with { Properties = raw };
            }).ToArray();
            return part with { Actions = actions, Decorations = decorations };
        }, "B-r2");
        string bSha = b.Commit("B edits decoration and raw event in region B");
        Check(bSha != aSha, "clients have genuinely diverged");

        ExpectRejected(() => SegmentGitRemoteSync.Push(b),
            "non-fast-forward B push cannot overwrite A history");
        Equal(aSha, RunGit(root, "--git-dir", bare, "rev-parse", "HEAD").Trim(),
            "remote must preserve A's commit after failed push");

        byte[] bContentBeforeFetch = File.ReadAllBytes(
            Path.Combine(bPath, "segments", "B.json"));
        Equal(aSha, SegmentGitRemoteSync.Fetch(b), "B fetches A commit");
        Sequence(bContentBeforeFetch,
            File.ReadAllBytes(Path.Combine(bPath, "segments", "B.json")),
            "fetch leaves B edits untouched");
        Equal(bSha, GitHead(bPath), "fetch does not change B HEAD");

        SegmentGitPullResult pull = SegmentGitRemoteSync.Pull(b);
        Check(pull.Success && pull.LocalCommit is not null,
            "two independent Segments should semantic-merge");
        string mergedSha = pull.LocalCommit!;
        string[] parents = RunGit(bPath, "show", "-s", "--format=%P", "HEAD")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Sequence([bSha, aSha], parents, "two parents preserve both author histories");
        Equal(mergedSha, GitHead(bPath), "B's HEAD points to the real merge commit");
        Equal(aSha, RunGit(root, "--git-dir", bare, "rev-parse", "HEAD").Trim(),
            "pull must not automatically push");
        Equal(mergedSha, SegmentGitRemoteSync.Push(b), "B publishes merged commit");

        Equal(mergedSha, SegmentGitRemoteSync.Fetch(a), "A fetches merged commit");
        SegmentGitPullResult aPull = SegmentGitRemoteSync.Pull(a);
        Check(aPull.Success, "A can fast-forward to the common merged revision");
        Equal(mergedSha, GitHead(aPath), "A HEAD contains the merged revision");

        SegmentGitSnapshot fromA = a.ReadAt(mergedSha);
        SegmentGitSnapshot fromB = b.ReadAt(mergedSha);
        Equal("Snare", fromA.Segments["A"].Actions.Single(
            action => action.EventType == "SetHitsound").HitSound,
            "A author's typed event survives semantic merge");
        Equal("B-edited", fromA.Segments["B"].Decorations.Single()
            .Properties["extra"]?["revision"]?.ToString(),
            "B author's edited decoration survives semantic merge");
        Equal(JsonSerializer.Serialize(fromA), JsonSerializer.Serialize(fromB),
            "clients agree on the identical merged Segment snapshot");

        SegmentGitAdoFaiPublication publication =
            SegmentGitAdoFaiPipeline.ExportPublishedHead(original, a, outputPath);
        Equal(mergedSha, publication.SourceCommit,
            "published file must be associated with exact merge commit");
        Equal(Path.GetFullPath(outputPath), publication.DestinationPath,
            "full destination path");
        Equal(original.FloorCount, publication.FloorCount, "published floor count");
        Equal(3, publication.ActionCount, "published action count");
        Check(File.Exists(outputPath), "verified .adofai exists");

        LevelDocument loaded = AdoFaiLoader.Load(outputPath).Document;
        Equal("Snare", loaded.ActionStore.Actions.Single(action =>
            action.EventType == "SetHitsound").HitSound, "A edits in exported chart");
        Equal(original.FloorCount, loaded.FloorCount,
            "export preserves all floors and midspin");
        Check(loaded.Angles.Contains(999d), "export preserves 999 midspin");
        JsonObject exported = JsonNode.Parse(File.ReadAllText(outputPath))!.AsObject();
        Equal("B-edited", exported["actions"]!.AsArray().OfType<JsonObject>()
            .Single(action => action["eventType"]?.ToString() == "CustomBackground")
            ["custom"]?["nested"]?["revision"]?.ToString(),
            "B's nested unknown raw event payload survives export");
        Equal("B-edited", exported["decorations"]![0]?["extra"]?["revision"]?.ToString(),
            "B's decoration edit survives export");
        Equal("123", exported["settings"]?["unknownSetting"]?["keep"]?.ToString(),
            "unknown project settings survive export");
        Equal("7", exported["customRoot"]?["future"]?[1]?["v"]?.ToString(),
            "unknown project root survives export");
        Equal("project-wide", exported["decorations"]![1]?["text"]?.ToString(),
            "project-wide decoration is not duplicated or deleted");

        SegmentEditedCompositionResult expected =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                original, fromA.Timeline, fromA.Segments);
        Check(expected.Accepted && expected.Timing is not null,
            "Git-merged sample assignments validate globally");
        TimingMap reopened = TimingMapBuilder.Build(loaded);
        for (int floor = 0; floor < original.FloorCount; floor++)
        {
            Near(expected.Timing!.GetEntryTime(floor), reopened.GetEntryTime(floor),
                "entry timing at floor " + floor);
            Near(expected.Timing.Floors[floor].ExitTime, reopened.Floors[floor].ExitTime,
                "exit timing at floor " + floor);
        }

        Sequence(sourceBefore, File.ReadAllBytes(sourcePath),
            "original raw template never overwritten");
        byte[] exportBeforeConflict = File.ReadAllBytes(outputPath);
        string indexBefore = File.Exists(Path.Combine(bPath, ".git", "index"))
            ? Convert.ToHexString(File.ReadAllBytes(Path.Combine(bPath, ".git", "index")))
            : "<absent>";

        // Neither a draft, nor a non-published branch tip is permitted to
        // replace the merged export without explicit reconciliation.
        EditSegment(a, "A", part =>
        {
            LevelAction[] actions = part.Actions.Select(action =>
                action.EventType == "SetHitsound"
                    ? action with { HitSound = "Hat" }
                    : action).ToArray();
            return part with { Actions = actions };
        }, "A-r3");
        string draftA = JsonSerializer.Serialize(a.Load());
        ExpectRejected(() => SegmentGitAdoFaiPipeline.ExportPublishedHead(
                original, a, outputPath, overwriteExisting: true),
            "dirty author A working tree must not be exported");
        Equal(draftA, JsonSerializer.Serialize(a.Load()), "dirty draft A is intact");
        string aNewCommit = a.Commit("A publishes conflicting A change");
        SegmentGitRemoteSync.Push(a);

        EditSegment(b, "A", part =>
        {
            LevelAction[] actions = part.Actions.Select(action =>
                action.EventType == "SetHitsound"
                    ? action with { HitSound = "Clap" }
                    : action).ToArray();
            return part with { Actions = actions };
        }, "A-r4");
        string bNewCommit = b.Commit("B independently edits A");
        byte[] bContentBeforeConflict = File.ReadAllBytes(
            Path.Combine(bPath, "segments", "A.json"));
        SegmentGitPullResult conflict = SegmentGitRemoteSync.Pull(b);
        Check(!conflict.Success && conflict.LocalCommit is null &&
              conflict.ContentConflictSegmentIds.Contains("A"),
            "conflicting A edits must not result in an implicit merge");
        Equal(bNewCommit, GitHead(bPath), "conflict keeps B HEAD");
        Equal(aNewCommit, RunGit(root, "--git-dir", bare, "rev-parse", "HEAD").Trim(),
            "conflict must not rewrite shared remote");
        Sequence(bContentBeforeConflict,
            File.ReadAllBytes(Path.Combine(bPath, "segments", "A.json")),
            "conflicting pull preserves B authored content");
        string indexAfter = File.Exists(Path.Combine(bPath, ".git", "index"))
            ? Convert.ToHexString(File.ReadAllBytes(Path.Combine(bPath, ".git", "index")))
            : "<absent>";
        Equal(indexBefore == "<absent>", indexAfter == "<absent>",
            "Git index existence consistent");
        ExpectRejected(() => SegmentGitAdoFaiPipeline.ExportPublishedHead(
                original, b, outputPath, overwriteExisting: true),
            "conflicted/unpublished B HEAD cannot replace published export");
        Sequence(exportBeforeConflict, File.ReadAllBytes(outputPath),
            "conflict and rejected export preserve previous .adofai bytes");

        // Even a clean published Git revision is not safe to export when a
        // local author changed music timing without updating sample ownership.
        SegmentGitSnapshot currentA = a.Load();
        LevelSegment currentSection = currentA.Segments["A"];
        LevelAction newSpeed = new(1, "SetSpeed", true, "Multiplier", null, 2, null);
        var changedA = currentA.Segments.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changedA["A"] = currentSection with
        {
            Actions = currentSection.Actions.Append(newSpeed).ToArray()
        };
        a.Save(new SegmentGitSnapshot(currentA.Timeline, changedA));
        string pendingStale = JsonSerializer.Serialize(a.Load());
        ExpectRejected(() => SegmentGitAdoFaiPipeline.ExportPublishedHead(
                original, a, outputPath, overwriteExisting: true),
            "newly modified timing in dirty owned workspace is not exportable");
        Equal(pendingStale, JsonSerializer.Serialize(a.Load()),
            "rejected dirty timing edit remains editable");
        string staleSha = a.Commit("A intentionally commits stale audio assignment");
        SegmentGitRemoteSync.Push(a);

        SegmentGitSnapshot stale = a.ReadAt(staleSha);
        SegmentEditedCompositionResult stalePreview =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                original, stale.Timeline, stale.Segments);
        Check(!stalePreview.Accepted &&
              stalePreview.Issues.Any(i =>
                  i.Kind == SegmentEditedCompositionIssueKind.SampleRangeMismatch),
            "stale global audio ownership is detected before export");
        ExpectRejected(() => SegmentGitAdoFaiPipeline.ExportPublishedHead(
                original, a, outputPath, overwriteExisting: true),
            "published but sample-inconsistent commit cannot replace export");
        Equal(staleSha, GitHead(aPath), "rejected export preserves latest A commit");
        Sequence(exportBeforeConflict, File.ReadAllBytes(outputPath),
            "failed export cannot overwrite prior working result");
        Sequence(sourceBefore, File.ReadAllBytes(sourcePath),
            "failed export cannot modify source");
    }

    private static void EditSegment(
        SegmentGitWorkspace workspace,
        string segmentId,
        Func<LevelSegment, LevelSegment> mutate,
        string revision)
    {
        SegmentGitSnapshot old = workspace.Load();
        var content = old.Segments.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        content[segmentId] = mutate(content[segmentId]);
        SegmentTimeTimeline timeline = old.Timeline with
        {
            Partitions = old.Timeline.Partitions.Select(part =>
                part.SegmentId == segmentId
                    ? part with { ContentRevisionId = revision }
                    : part).ToArray()
        };
        workspace.Save(new SegmentGitSnapshot(timeline, content));
    }

    private static SegmentTimeTimeline Allocations(LevelDocument chart)
    {
        TimingMap timing = TimingMapBuilder.Build(chart);
        (string Id, int Start, int End)[] cuts =
        [
            ("A", 0, 3), ("B", 3, chart.FloorCount)
        ];
        return new SegmentTimeTimeline(SampleRate, cuts.Select(cut =>
        {
            double beginning = PlaybackClock.ChartToAudioTime(
                chart, timing.GetEntryTime(cut.Start));
            double finishing = PlaybackClock.ChartToAudioTime(
                chart, cut.End == chart.FloorCount
                    ? timing.Duration : timing.GetEntryTime(cut.End));
            long start = checked((long)Math.Round(beginning * SampleRate,
                MidpointRounding.AwayFromZero));
            long end = checked((long)Math.Round(finishing * SampleRate,
                MidpointRounding.AwayFromZero));
            return new SegmentTimePartition(cut.Id, start, end,
                cut.Id + "-r1", start, "author-" + cut.Id,
                SegmentPartitionStatus.Active);
        }).ToArray());
    }

    private static string GitHead(string dir) =>
        RunGit(dir, "rev-parse", "--verify", "HEAD").Trim();

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
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start Git.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Git process timed out.");
        }
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} exited {process.ExitCode}: {error}");
        return stdout;
    }

    private static void CleanupRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to follow linked test directory.");
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attr = File.GetAttributes(entry);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing to follow linked test entry.");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"WARNING: could not clean isolated E2E temp repo '{root}': {ex.Message}");
        }
    }

    private static void ExpectRejected(Action action, string description)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsafe operation accepted: " + description);
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
            throw new InvalidOperationException("Sequence mismatch: " + description);
    }

    private static void Near(double expected, double actual, string description)
    {
        if (Math.Abs(expected - actual) > 1e-7)
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
