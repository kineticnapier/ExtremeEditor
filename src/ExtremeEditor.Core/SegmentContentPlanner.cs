namespace ExtremeEditor.Core;

/// <summary>
/// A segment's authored-content state is separate from the audio-time allocation.
/// Empty is an explicit, legitimate payload state, not a missing dictionary key.
/// FloorAligned represents existing PoC payloads; future time-first authored
/// content need not be limited to a floor-aligned LevelSegment.
/// </summary>
public enum SegmentContentKind : byte
{
    Empty,
    FloorAligned
}

/// <summary>
/// An explicit content slot for one stable SegmentId. An empty segment owns
/// no chart tiles and needs no invented floor or entry angle.
/// RevisionId must agree with its audio-time partition.
/// </summary>
public sealed record SegmentContentEntry(
    string SegmentId,
    SegmentContentKind Kind,
    string? RevisionId,
    LevelSegment? FloorAlignedContent);

public enum SegmentContentIssueKind : byte
{
    InvalidTimeline,
    MissingActiveEntry,
    InvalidContentEntry,
    RevisionMismatch,
    UnknownAllocation
}

public sealed record SegmentContentIssue(
    SegmentContentIssueKind Kind,
    string? SegmentId,
    string Detail);

public sealed record SegmentContentValidationResult(
    IReadOnlyList<SegmentContentIssue> Issues)
{
    public bool Accepted => Issues.Count == 0;
}

/// <summary>
/// RED contract for first-class time-owned empty Segment content.
/// This does not merge, export, persist, or synthesize ADOFAI floors.
/// </summary>
public static class SegmentContentPlanner
{
    public static SegmentContentEntry CreateEmpty(SegmentTimePartition partition) =>
        throw new NotImplementedException(
            "RED: create an explicit empty content entry for a valid audio-time allocation.");

    public static SegmentContentValidationResult Validate(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentContentEntry> entries) =>
        throw new NotImplementedException(
            "RED: distinguish legitimate empty content from missing or invalid content entries.");
}
