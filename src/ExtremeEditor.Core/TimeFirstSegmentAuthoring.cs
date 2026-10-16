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

public sealed class TimeFirstSegmentEditorSession
{
    public TimeFirstAuthoredSegment Current => throw new NotImplementedException();
    public bool CanUndo => false;
    public bool CanRedo => false;

    public static TimeFirstSegmentEditorSession Open(
        TimeFirstSegmentSnapshot snapshot, string segmentId, string ownerId) =>
        throw new NotImplementedException(
            "RED: open an owner-scoped real time-first tile editor.");

    public void PlaceTile(
        string tileId, long audioSample, double destinationAngle) =>
        throw new NotImplementedException();

    public void ReplaceActions(
        string tileId, IReadOnlyList<LevelAction> actions) =>
        throw new NotImplementedException();

    public void RemoveTile(string tileId) => throw new NotImplementedException();
    public void Undo() => throw new NotImplementedException();
    public void Redo() => throw new NotImplementedException();

    public TimeFirstSegmentSnapshot CreateRevision(
        TimeFirstSegmentSnapshot source, string revisionId) =>
        throw new NotImplementedException();
}
