namespace ExtremeEditor.Core;

/// <summary>
/// A time-positioned draft marker for one future ADOFAI tile.
/// AudioSample is an absolute position on the shared decoded audio timeline.
/// It is NOT an ADOFAI floor, angle, or proven hit time after composition.
/// </summary>
public sealed record SegmentDraftTileAnchor(
    string TileId,
    long AudioSample);

/// <summary>
/// An isolated editable draft opened from an EMPTY time-owned Segment.
/// No floor or angle is invented until its musical entry/exit state can
/// actually be resolved in a separate composition phase.
/// </summary>
public sealed record SegmentTimeDraft(
    string SegmentId,
    string OwnerId,
    int SampleRate,
    long StartSample,
    long EndSampleExclusive,
    IReadOnlyList<SegmentDraftTileAnchor> TileAnchors)
{
    public bool IsEmpty => TileAnchors.Count == 0;
}

/// <summary>
/// RED: editing time-anchored tile drafts in explicitly empty regions.
/// This API must not mutate workspace allocations, imported chart content,
/// other owners' notes, source files, or any Git repository.
/// </summary>
public static class SegmentTimeDraftEditor
{
    public static SegmentTimeDraft Open(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentContentEntry> entries,
        string segmentId,
        string ownerId) =>
        throw new NotImplementedException(
            "RED: open an owned empty Segment as a tile-free audio-time draft.");

    public static SegmentTimeDraft PlaceAnchor(
        SegmentTimeDraft draft, string tileId, long audioSample) =>
        throw new NotImplementedException(
            "RED: place a tile timing anchor inside the owned half-open audio-time interval.");

    public static SegmentTimeDraft RemoveAnchor(
        SegmentTimeDraft draft, string tileId) =>
        throw new NotImplementedException(
            "RED: remove a draft tile anchor without destroying the owning Segment.");
}
