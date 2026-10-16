using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TimeFirstSegmentWorkspaceRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-time-workspace-{Guid.NewGuid():N}");
        string legacy = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-legacy-workspace-{Guid.NewGuid():N}");
        try
        {
            TimeFirstSegmentSnapshot source = Fixture();
            string before = JsonSerializer.Serialize(source);

            TimeFirstSegmentGitWorkspace workspace =
                TimeFirstSegmentGitWorkspace.Initialize(root);
            workspace.Save(source);
            string commit = workspace.Commit("Create time-first project");
            Check(commit.Length is 40 or 64, "commit must return an immutable hash");

            TimeFirstSegmentSnapshot loaded =
                TimeFirstSegmentGitWorkspace.Open(root).Load();
            Check(loaded.Audio == source.Audio, "audio identity roundtrip");
            Check(loaded.TimingPoints.SequenceEqual(source.TimingPoints),
                "musical timing points roundtrip");
            Check(loaded.Timeline.Partitions.SequenceEqual(source.Timeline.Partitions),
                "sample-time allocations roundtrip");
            Check(loaded.Content.Values.All(entry =>
                    entry.Kind == SegmentContentKind.Empty),
                "all-empty project remains explicitly empty");
            Check(loaded.Drafts["B"].TileAnchors.Select(anchor => anchor.TileId)
                    .SequenceEqual(["same-1", "same-2"]),
                "equal-time anchor order roundtrip");
            Check(before == JsonSerializer.Serialize(source),
                "persistence must not mutate caller state");

            File.Delete(Path.Combine(root, "time-first-segments", "B.json"));
            ExpectMissing(() => workspace.Load(),
                "missing content file must not become an empty Segment");

            SegmentGitWorkspace.Initialize(legacy);
            ExpectRejected(() => TimeFirstSegmentGitWorkspace.Open(legacy),
                "legacy workspace must remain distinct and unchanged");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
        }
    }

    private static TimeFirstSegmentSnapshot Fixture()
    {
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "A", 0, Rate * 10L, "alice");
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "B", Rate * 10L, Rate * 20L, "bob");
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "C", Rate * 20L, Rate * 30L, "alice");

        var content = timeline.Partitions.ToDictionary(
            partition => partition.SegmentId,
            SegmentContentPlanner.CreateEmpty,
            StringComparer.Ordinal);
        var drafts = timeline.Partitions.ToDictionary(
            partition => partition.SegmentId,
            partition => SegmentTimeDraftEditor.Open(
                timeline, content, partition.SegmentId, partition.OwnerId!),
            StringComparer.Ordinal);
        drafts["B"] = SegmentTimeDraftEditor.PlaceAnchor(
            drafts["B"], "same-1", Rate * 15L);
        drafts["B"] = SegmentTimeDraftEditor.PlaceAnchor(
            drafts["B"], "same-2", Rate * 15L);

        return new TimeFirstSegmentSnapshot(
            new TimeFirstAudioIdentity(
                "song.ogg",
                new string('a', 64),
                Rate,
                Rate * 30L),
            [
                new MusicTimingPoint(0, 120m),
                new MusicTimingPoint(Rate * 16L, 180m)
            ],
            timeline,
            content,
            drafts);
    }

    private static void ExpectMissing(Action action, string label)
    {
        try { action(); }
        catch (TimeFirstSegmentContentMissingException) { return; }
        throw new InvalidOperationException("Missing payload accepted: " + label);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidDataException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe workspace operation accepted: " + label);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
