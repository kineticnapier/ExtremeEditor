using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TimeFirstSegmentAuthoringRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        TimeFirstSegmentSnapshot empty = Fixture();
        var editor = TimeFirstSegmentEditorSession.Open(empty, "A", "alice");
        Check(editor.Current.Tiles.Count == 0, "explicit empty draft opens empty");

        editor.PlaceTile("one", Rate * 2L, 0);
        editor.PlaceTile("same", Rate * 2L, 90);
        editor.PlaceTile("later", Rate * 4L, 180);
        editor.ReplaceActions("same",
        [
            new LevelAction(2, "Twirl", true, null, null, null, null)
        ]);
        Sequence(["one", "same", "later"],
            editor.Current.Tiles.Select(tile => tile.TileId),
            "equal-time insertion order");
        Check(editor.CanUndo && !editor.CanRedo, "edit history state");

        editor.RemoveTile("later");
        editor.Undo();
        Check(editor.Current.Tiles.Any(tile => tile.TileId == "later"),
            "Undo restores removed real tile");
        editor.Redo();
        Check(editor.Current.Tiles.All(tile => tile.TileId != "later"),
            "Redo reapplies removal");
        editor.Undo();

        TimeFirstSegmentSnapshot authored =
            editor.CreateRevision(empty, "A-r1");
        Check(authored.Timeline.Partitions.Single(p => p.SegmentId == "A")
                .ContentRevisionId == "A-r1",
            "allocation points at exact authored revision");
        Check(authored.Content["A"].Kind == SegmentContentKind.TimeAuthored,
            "content state is no longer empty");
        Check(authored.Authored?["A"].Tiles.Count == 3,
            "real tile geometry is stored separately from anchors");

        ExpectRejected(() => TimeFirstSegmentEditorSession.Open(
            empty, "A", "bob"), "other owner cannot edit");
        ExpectRejected(() => editor.PlaceTile("outside", Rate * 10L, 0),
            "exclusive Segment end");
        ExpectRejected(() => editor.PlaceTile("one", Rate * 3L, 0),
            "duplicate tile ID");
        ExpectRejected(() => editor.PlaceTile("bad-angle", Rate * 3L, double.NaN),
            "non-finite geometry");
        ExpectRejected(() => editor.ReplaceActions("same",
        [
            new LevelAction(99, "Twirl", true, null, null, null, null)
        ]), "action floor outside authored Segment");

        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-time-authoring-{Guid.NewGuid():N}");
        try
        {
            TimeFirstSegmentGitWorkspace workspace =
                TimeFirstSegmentGitWorkspace.Initialize(root);
            workspace.Save(authored);
            workspace.Commit("Author A");
            TimeFirstSegmentSnapshot reopened =
                TimeFirstSegmentGitWorkspace.Open(root).Load();
            Check(reopened.Authored?["A"].Tiles.Select(tile => tile.TileId)
                    .SequenceEqual(["one", "same", "later"]) == true,
                "authored geometry/actions survive save and reopen");
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    private static TimeFirstSegmentSnapshot Fixture()
    {
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "A", 0, Rate * 10L, "alice");
        timeline = SegmentTimePlanner.CreateEmpty(
            timeline, "B", Rate * 10L, Rate * 20L, "bob");
        var content = timeline.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty,
            StringComparer.Ordinal);
        var drafts = timeline.Partitions.ToDictionary(
            p => p.SegmentId,
            p => SegmentTimeDraftEditor.Open(
                timeline, content, p.SegmentId, p.OwnerId!),
            StringComparer.Ordinal);
        return new TimeFirstSegmentSnapshot(
            new TimeFirstAudioIdentity("song.ogg", new string('b', 64),
                Rate, Rate * 20L),
            [new MusicTimingPoint(0, 120m)],
            timeline, content, drafts);
    }

    private static void CleanupRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked test entry.");
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
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe authoring accepted: " + label);
    }

    private static void Sequence<T>(IEnumerable<T> expected,
        IEnumerable<T> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Unexpected sequence: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
