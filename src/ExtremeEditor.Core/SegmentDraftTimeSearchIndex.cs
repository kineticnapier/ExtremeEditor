namespace ExtremeEditor.Core;

/// <summary>
/// An exact song-sample query in an independently authored time Segment.
/// SegmentId is null only when the sample is outside every active allocation.
/// A non-null SegmentId with zero matches means an assigned section has no
/// tile marker at this sample. Markers are not playable ADOFAI floor events.
/// </summary>
public sealed record SegmentDraftTimeMatch(
    string? SegmentId,
    IReadOnlyList<SegmentDraftTileAnchor> Anchors)
{
    public bool IsAssigned => SegmentId is not null;
    public bool HasAnchors => Anchors.Count > 0;
}

/// <summary>
/// RED: immutable, pre-indexed lookup over active time-first draft Segments.
/// Building an index validates and freezes sorted allocations and per-Segment
/// monotone audio-sample markers; queries must not merge across boundaries.
/// </summary>
public sealed class SegmentDraftTimeSearchIndex
{
    private SegmentDraftTimeSearchIndex() { }

    public static SegmentDraftTimeSearchIndex Build(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentTimeDraft> drafts) =>
        throw new NotImplementedException(
            "RED: build a boundary-aware time index for independently authored Segment drafts.");

    /// <summary>
    /// Look up the containing active Segment, then use lower/upper bounds in
    /// that Segment only. An unallocated gap returns SegmentId = null.
    /// </summary>
    public SegmentDraftTimeMatch FindAt(long audioSample) =>
        throw new NotImplementedException(
            "RED: search a song sample within one monotone Segment.");

    /// <summary>
    /// Search [start,end) within exactly one Segment. Crossing even one
    /// allocation boundary is a contract violation, not an implicit merge.
    /// </summary>
    public IReadOnlyList<SegmentDraftTileAnchor> FindRange(
        string segmentId, long startSample, long endSampleExclusive) =>
        throw new NotImplementedException(
            "RED: search a half-open tile range without crossing Segment boundaries.");
}
