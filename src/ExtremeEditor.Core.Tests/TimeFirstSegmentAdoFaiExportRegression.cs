using System.Security.Cryptography;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TimeFirstSegmentAdoFaiExportRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-time-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            byte[] audioBytes = [0x11, 0x22, 0x33, 0x44];
            string songPath = Path.Combine(root, "song.ogg");
            File.WriteAllBytes(songPath, audioBytes);
            string hash = Convert.ToHexString(SHA256.HashData(audioBytes)).ToLowerInvariant();
            TimeFirstSegmentSnapshot snapshot = Fixture(hash);
            var settings = new TimeFirstAdoFaiSettings(InitialBpm: 120);
            TimeFirstAdoFaiCompositionResult composition =
                TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(snapshot, settings);
            Check(composition.Accepted, "fully authored fixture is complete");
            Check(composition.Document!.Angles.SequenceEqual([0d, 0d]),
                "global authored ADOFAI angles");
            Check(composition.Document.FloorCount == 3,
                "one starting floor plus two authored destinations");
            Check(composition.Document.ActionStore.Actions.Any(a =>
                a.Floor == 2 && a.EventType == "SetHitsound"),
                "second Segment actions remap to global floor");

            TimeFirstSegmentGitWorkspace workspace =
                TimeFirstSegmentGitWorkspace.Initialize(Path.Combine(root, "workspace"));
            workspace.Save(snapshot);
            string committed = workspace.Commit("complete chart");
            string destination = Path.Combine(root, "finished.adofai");
            TimeFirstAdoFaiExportResult result =
                TimeFirstSegmentAdoFaiExporter.ExportValidated(
                    workspace, settings, destination);
            Check(result.SourceCommit == committed && result.FloorCount == 3,
                "only the exact committed workspace was exported");
            LevelDocument reopened = AdoFaiLoader.Load(destination).Document;
            Check(reopened.FloorCount == 3 && reopened.Angles.SequenceEqual([0d, 0d]),
                "export roundtrips through production loader");
            Check(reopened.ActionStore.Actions.Any(a =>
                a.Floor == 2 && a.EventType == "SetHitsound"),
                "remapped event survives readback");
            TimingMap time = TimingMapBuilder.Build(reopened);
            Check(Math.Abs(PlaybackClock.ChartToAudioTime(reopened,
                time.GetEntryTime(1)) - 0.5) <= 1d / Rate,
                "A tile sample timing survived readback");
            Check(Math.Abs(PlaybackClock.ChartToAudioTime(reopened,
                time.GetEntryTime(2)) - 1.0) <= 1d / Rate,
                "B tile sample timing survived readback");

            byte[] firstOutput = File.ReadAllBytes(destination);
            ExpectRejected(() => TimeFirstSegmentAdoFaiExporter.ExportValidated(
                workspace, settings, destination), "cannot overwrite without consent");
            Check(File.ReadAllBytes(destination).SequenceEqual(firstOutput),
                "existing output preserved");

            TimeFirstSegmentSnapshot mistimed = Edit(snapshot, "B", "bob",
                "B-r2", "b-note2", Rate + 250, 90);
            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                mistimed, settings).Accepted,
                "sample anchor cannot silently diverge from angle-derived timing");
            workspace.Save(mistimed);
            ExpectRejected(() => TimeFirstSegmentAdoFaiExporter.ExportValidated(
                workspace, settings, destination, overwriteExisting: true),
                "uncommitted authored data blocks publication");
            Check(File.ReadAllBytes(destination).SequenceEqual(firstOutput),
                "rejected publication never modifies existing output");
            workspace.Save(snapshot);

            var emptyParts = snapshot.Timeline.Partitions.Select(p => p.SegmentId == "B"
                ? p with { ContentRevisionId = null } : p).ToArray();
            SegmentTimeTimeline emptyTimeline = new(Rate, emptyParts);
            var emptyContent = snapshot.Content.ToDictionary(x => x.Key, x => x.Value);
            emptyContent["B"] = SegmentContentPlanner.CreateEmpty(
                emptyTimeline.Partitions.Single(p => p.SegmentId == "B"));
            var emptyAuthored = snapshot.Authored!.ToDictionary(x => x.Key, x => x.Value);
            emptyAuthored.Remove("B");
            // An empty segment must also have an empty draft and no authored revision.
            var emptyDrafts = snapshot.Drafts.ToDictionary(x => x.Key, x => x.Value);
            emptyDrafts["B"] = SegmentTimeDraftEditor.Open(
                emptyTimeline, emptyContent, "B", "bob");
            TimeFirstSegmentSnapshot incomplete = snapshot with
            {
                Timeline = emptyTimeline,
                Content = emptyContent, Drafts = emptyDrafts, Authored = emptyAuthored
            };
            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                incomplete, settings).Accepted, "an assigned empty Segment is not complete");
            var rotated = TimeFirstSegmentEditorSession.Open(snapshot, "B", "bob");
            rotated.SetEntryAngle(0);
            TimeFirstSegmentSnapshot seamBroken = rotated.CreateRevision(snapshot, "B-entry-mismatch");
            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                seamBroken, settings).Accepted,
                "incompatible Segment entry orientation is rejected");
            var unsafeEvent = TimeFirstSegmentEditorSession.Open(snapshot, "B", "bob");
            unsafeEvent.ReplaceActions("b-note", [new LevelAction(1, "PositionTrack", true,
                null, null, null, null)]);
            TimeFirstSegmentSnapshot unsupported = unsafeEvent.CreateRevision(snapshot, "B-unsafe");
            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                unsupported, settings).Accepted,
                "unverified cross-boundary effects must fail closed");

            File.WriteAllBytes(songPath, [0xAA, 0xBB]);
            ExpectRejected(() => TimeFirstSegmentAdoFaiExporter.ExportValidated(
                workspace, settings, destination, overwriteExisting: true),
                "substituting the measured audio cannot silently publish");
            Check(File.ReadAllBytes(destination).SequenceEqual(firstOutput),
                "audio hash mismatch keeps the previous export intact");
            File.WriteAllBytes(songPath, audioBytes);

            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                snapshot, settings with { InitialBpm = 240 }).Accepted,
                "an unrelated music BPM cannot substitute for chart timing");
            Check(!TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(
                snapshot, settings with { OffsetMilliseconds = 25 }).Accepted,
                "ADOFAI offset must be accounted for explicitly");
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    private static TimeFirstSegmentSnapshot Fixture(string hash)
    {
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "A", 0, Rate, "alice");
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "B", Rate, Rate * 2L, "bob");
        var content = timeline.Partitions.ToDictionary(p => p.SegmentId,
            SegmentContentPlanner.CreateEmpty, StringComparer.Ordinal);
        var drafts = timeline.Partitions.ToDictionary(p => p.SegmentId,
            p => SegmentTimeDraftEditor.Open(timeline, content, p.SegmentId, p.OwnerId!),
            StringComparer.Ordinal);
        TimeFirstSegmentSnapshot snapshot = new(
            new TimeFirstAudioIdentity("song.ogg", hash, Rate, Rate * 2L),
            [new MusicTimingPoint(0, 120m)], timeline, content, drafts);
        var a = TimeFirstSegmentEditorSession.Open(snapshot, "A", "alice");
        a.SetEntryAngle(270);
        a.PlaceTile("a-note", Rate / 2L, 0);
        snapshot = a.CreateRevision(snapshot, "A-r1");
        var b = TimeFirstSegmentEditorSession.Open(snapshot, "B", "bob");
        b.SetEntryAngle(270);
        b.PlaceTile("b-note", Rate, 0);
        b.ReplaceActions("b-note", [new LevelAction(1, "SetHitsound", true,
            null, null, null, null) { HitSound = "Kick" }]);
        return b.CreateRevision(snapshot, "B-r1");
    }

    private static TimeFirstSegmentSnapshot Edit(TimeFirstSegmentSnapshot snapshot,
        string id, string owner, string revision, string tileId, long sample, double angle)
    {
        var editor = TimeFirstSegmentEditorSession.Open(snapshot, id, owner);
        editor.PlaceTile(tileId, sample, angle);
        return editor.CreateRevision(snapshot, revision);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsafe export accepted: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Failure: " + label);
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
                    throw new IOException("Refusing linked test fixture.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(root, recursive: true);
    }
}
