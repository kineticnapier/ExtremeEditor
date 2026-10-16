namespace ExtremeEditor.Core;

public sealed record TimeFirstAuthoredTile(
    string TileId,
    long AudioSample,
    double DestinationAngle,
    IReadOnlyList<LevelAction> Actions);

public sealed record TimeFirstAuthoredSegment(
    string SegmentId,
    string OwnerId,
    int SampleRate,
    long StartSample,
    long EndSampleExclusive,
    string RevisionId,
    double EntryAngle,
    IReadOnlyList<TimeFirstAuthoredTile> Tiles);

/// <summary>
/// Owner-scoped, undoable authoring over a single time allocation. Audio
/// anchors remain explicit, while every authored destination tile also has
/// concrete ADOFAI geometry and floor-local actions.
/// </summary>
public sealed class TimeFirstSegmentEditorSession
{
    private readonly Stack<TimeFirstAuthoredSegment> _undo = new();
    private readonly Stack<TimeFirstAuthoredSegment> _redo = new();
    private TimeFirstAuthoredSegment _current;

    private TimeFirstSegmentEditorSession(TimeFirstAuthoredSegment current) =>
        _current = Freeze(current);

    public TimeFirstAuthoredSegment Current => _current;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool IsDirty => _undo.Count != 0 || _redo.Count != 0;

    public static TimeFirstSegmentEditorSession Open(
        TimeFirstSegmentSnapshot snapshot, string segmentId, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(snapshot);

        SegmentTimePartition partition = snapshot.Timeline.Partitions
            .SingleOrDefault(item => string.Equals(
                item.SegmentId, segmentId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The requested Segment does not exist.", nameof(segmentId));
        if (partition.Status != SegmentPartitionStatus.Active ||
            !string.Equals(partition.OwnerId, ownerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Only the owner of an active Segment may edit its real tile content.");

        TimeFirstAuthoredSegment current;
        if (snapshot.Authored is not null &&
            snapshot.Authored.TryGetValue(segmentId, out TimeFirstAuthoredSegment? authored))
        {
            current = authored;
        }
        else
        {
            SegmentContentEntry content = snapshot.Content[segmentId];
            if (content.Kind != SegmentContentKind.Empty ||
                partition.ContentRevisionId is not null ||
                snapshot.Drafts[segmentId].TileAnchors.Count != 0)
                throw new InvalidOperationException(
                    "New real tile authoring requires an explicitly empty Segment draft.");
            current = new TimeFirstAuthoredSegment(
                segmentId, ownerId, snapshot.Timeline.SampleRate,
                partition.StartSample, partition.EndSampleExclusive,
                RevisionId: "draft", EntryAngle: 0,
                Tiles: Array.AsReadOnly(Array.Empty<TimeFirstAuthoredTile>()));
        }

        Validate(current);
        return new TimeFirstSegmentEditorSession(current);
    }

    public void SetEntryAngle(double angle)
    {
        CheckAngle(angle, nameof(angle));
        Apply(_current with { EntryAngle = NormalizeAngle(angle) });
    }

    public void PlaceTile(string tileId, long audioSample, double destinationAngle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);
        CheckAngle(destinationAngle, nameof(destinationAngle));
        if (audioSample < _current.StartSample ||
            audioSample >= _current.EndSampleExclusive)
            throw new ArgumentOutOfRangeException(nameof(audioSample),
                "A real tile must remain inside its owned half-open Segment.");
        if (_current.Tiles.Any(tile => string.Equals(
                tile.TileId, tileId, StringComparison.Ordinal)))
            throw new ArgumentException("Tile IDs must be unique within a Segment.", nameof(tileId));

        int index = UpperBound(_current.Tiles, audioSample);
        var tiles = _current.Tiles.Select(Freeze).ToList();
        tiles.Insert(index, new TimeFirstAuthoredTile(
            tileId, audioSample, NormalizeAngle(destinationAngle),
            Array.AsReadOnly(Array.Empty<LevelAction>())));
        Apply(_current with { Tiles = FreezeAndNormalize(tiles) });
    }

    public void ReplaceActions(string tileId, IReadOnlyList<LevelAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);
        ArgumentNullException.ThrowIfNull(actions);
        int index = FindTile(tileId);
        int expectedFloor = index + 1;
        if (actions.Any(action => action is null || action.Floor != expectedFloor))
            throw new ArgumentException(
                "Every local tile action must target its exact authored floor.", nameof(actions));

        var tiles = _current.Tiles.Select(Freeze).ToArray();
        tiles[index] = tiles[index] with
        {
            Actions = Array.AsReadOnly(actions.Select(CloneAction).ToArray())
        };
        Apply(_current with { Tiles = FreezeAndNormalize(tiles) });
    }

    public void RemoveTile(string tileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);
        int index = FindTile(tileId);
        var tiles = _current.Tiles.Select(Freeze).ToList();
        tiles.RemoveAt(index);
        Apply(_current with { Tiles = FreezeAndNormalize(tiles) });
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            throw new InvalidOperationException("There is no time-first edit to undo.");
        _redo.Push(_current);
        _current = _undo.Pop();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            throw new InvalidOperationException("There is no time-first edit to redo.");
        _undo.Push(_current);
        _current = _redo.Pop();
    }

    public TimeFirstSegmentSnapshot CreateRevision(
        TimeFirstSegmentSnapshot source, string revisionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(source);
        if (revisionId.Length > 128 || revisionId.Any(char.IsControl))
            throw new ArgumentException("The authored revision ID is invalid.", nameof(revisionId));

        SegmentTimePartition allocation = source.Timeline.Partitions.SingleOrDefault(
            item => string.Equals(item.SegmentId, _current.SegmentId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The edited allocation no longer exists.");
        if (allocation.Status != SegmentPartitionStatus.Active ||
            allocation.OwnerId != _current.OwnerId ||
            allocation.StartSample != _current.StartSample ||
            allocation.EndSampleExclusive != _current.EndSampleExclusive ||
            source.Timeline.SampleRate != _current.SampleRate)
            throw new InvalidOperationException(
                "The edited Segment ownership or time range changed before save.");

        TimeFirstAuthoredSegment authored = Freeze(
            _current with { RevisionId = revisionId });
        Validate(authored);
        var partitions = source.Timeline.Partitions.Select(partition =>
            partition.SegmentId == authored.SegmentId
                ? partition with { ContentRevisionId = revisionId }
                : partition with { }).ToArray();
        var timeline = new SegmentTimeTimeline(
            source.Timeline.SampleRate, Array.AsReadOnly(partitions));

        var content = source.Content.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        content[authored.SegmentId] = new SegmentContentEntry(
            authored.SegmentId, SegmentContentKind.TimeAuthored,
            revisionId, null, authored);

        var drafts = source.Drafts.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        drafts[authored.SegmentId] = new SegmentTimeDraft(
            authored.SegmentId, authored.OwnerId, authored.SampleRate,
            authored.StartSample, authored.EndSampleExclusive,
            Array.AsReadOnly(authored.Tiles.Select(tile =>
                new SegmentDraftTileAnchor(tile.TileId, tile.AudioSample)).ToArray()));

        var authoredMap = source.Authored?.ToDictionary(
            pair => pair.Key, pair => Freeze(pair.Value), StringComparer.Ordinal)
            ?? new Dictionary<string, TimeFirstAuthoredSegment>(StringComparer.Ordinal);
        authoredMap[authored.SegmentId] = authored;

        var result = source with
        {
            Timeline = timeline,
            Content = content,
            Drafts = drafts,
            Authored = authoredMap
        };
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(result);
        _current = authored;
        _undo.Clear();
        _redo.Clear();
        return result;
    }

    public static void Validate(TimeFirstAuthoredSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (string.IsNullOrWhiteSpace(segment.SegmentId) ||
            string.IsNullOrWhiteSpace(segment.OwnerId) ||
            string.IsNullOrWhiteSpace(segment.RevisionId) ||
            segment.SampleRate <= 0 || segment.StartSample < 0 ||
            segment.EndSampleExclusive <= segment.StartSample ||
            !double.IsFinite(segment.EntryAngle) || segment.Tiles is null)
            throw new ArgumentException("The authored Segment metadata is invalid.", nameof(segment));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        long previous = segment.StartSample;
        for (int i = 0; i < segment.Tiles.Count; i++)
        {
            TimeFirstAuthoredTile? tile = segment.Tiles[i];
            if (tile is null || string.IsNullOrWhiteSpace(tile.TileId) ||
                !ids.Add(tile.TileId) || tile.AudioSample < segment.StartSample ||
                tile.AudioSample >= segment.EndSampleExclusive ||
                tile.AudioSample < previous || !double.IsFinite(tile.DestinationAngle) ||
                tile.Actions is null || tile.Actions.Any(action =>
                    action is null || action.Floor != i + 1))
                throw new ArgumentException(
                    "Authored tiles must have unique IDs, monotone in-range samples, finite geometry, and exact local action floors.",
                    nameof(segment));
            previous = tile.AudioSample;
        }
    }

    internal static TimeFirstAuthoredSegment Freeze(TimeFirstAuthoredSegment segment) =>
        segment with
        {
            Tiles = Array.AsReadOnly(segment.Tiles.Select(Freeze).ToArray())
        };

    private void Apply(TimeFirstAuthoredSegment next)
    {
        Validate(next);
        _undo.Push(_current);
        _current = Freeze(next);
        _redo.Clear();
    }

    private int FindTile(string tileId)
    {
        for (int i = 0; i < _current.Tiles.Count; i++)
            if (string.Equals(_current.Tiles[i].TileId, tileId, StringComparison.Ordinal))
                return i;
        throw new ArgumentException("The authored tile does not exist.", nameof(tileId));
    }

    private static int UpperBound(IReadOnlyList<TimeFirstAuthoredTile> tiles, long sample)
    {
        int lo = 0;
        int hi = tiles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (tiles[mid].AudioSample <= sample) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static IReadOnlyList<TimeFirstAuthoredTile> FreezeAndNormalize(
        IEnumerable<TimeFirstAuthoredTile> source)
    {
        TimeFirstAuthoredTile[] tiles = source.Select(Freeze).ToArray();
        for (int i = 0; i < tiles.Length; i++)
        {
            int floor = i + 1;
            tiles[i] = tiles[i] with
            {
                Actions = Array.AsReadOnly(tiles[i].Actions.Select(action =>
                    CloneAction(action with { Floor = floor })).ToArray())
            };
        }
        return Array.AsReadOnly(tiles);
    }

    private static TimeFirstAuthoredTile Freeze(TimeFirstAuthoredTile tile) =>
        tile with
        {
            Actions = Array.AsReadOnly(tile.Actions.Select(CloneAction).ToArray())
        };

    private static LevelAction CloneAction(LevelAction action) => action with
    {
        SourceProperties = action.SourceProperties?.DeepClone()
            as System.Text.Json.Nodes.JsonObject,
        PropertyOverrides = action.PropertyOverrides?.DeepClone()
            as System.Text.Json.Nodes.JsonObject
    };

    private static void CheckAngle(double angle, string parameter)
    {
        if (!double.IsFinite(angle))
            throw new ArgumentOutOfRangeException(parameter,
                "ADOFAI tile angles must be finite.");
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }
}
