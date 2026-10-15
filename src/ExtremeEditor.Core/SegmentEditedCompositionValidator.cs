namespace ExtremeEditor.Core;

/// <summary>
/// A rejected edited composition is not a candidate for .adofai export.
/// Semantic validation is intentionally limited to the currently modeled
/// floor-aligned chart; it does not establish stock-game playability.
/// </summary>
public enum SegmentEditedCompositionIssueKind : byte
{
    MissingActiveContent,
    InvalidFloorCoverage,
    SampleRangeMismatch,
    UnsupportedCrossBoundaryEffect,
    InvalidModeledState
}

public sealed record SegmentEditedCompositionIssue(
    SegmentEditedCompositionIssueKind Kind,
    string? SegmentId,
    int Floor,
    string Detail);

/// <summary>
/// On failure both Document and Timing are null; the original inputs (even
/// rejected pending edits) remain unchanged. On success the caller receives
/// an independent in-memory candidate, NOT an exported .adofai file.
/// </summary>
public sealed record SegmentEditedCompositionResult(
    LevelDocument? Document,
    TimingMap? Timing,
    IReadOnlyList<SegmentEditedCompositionIssue> Issues)
{
    public bool Accepted =>
        Document is not null && Timing is not null && Issues.Count == 0;
}

/// <summary>
/// RED-only validation for independently EDITED floor-aligned Segments.
/// Unlike SegmentBoundaryValidator.ValidateRoundTrip, the edited chart is
/// expected to differ from the original template.
///
/// Each active allocation is explicitly paired with a content SegmentId.
/// Its audio-sample range must align with the composed chart's corresponding
/// floor-entry/exit boundary. Parked revisions must not participate in the
/// composition, and no arbitrary mid-floor audio cut is supported.
/// </summary>
public static class SegmentEditedCompositionValidator
{
    public static SegmentEditedCompositionResult ComposeAndValidate(
        LevelDocument template,
        SegmentTimeTimeline allocations,
        IReadOnlyDictionary<string, LevelSegment> contentBySegmentId) =>
        throw new NotImplementedException(
            "RED: compose independently edited Segment payloads, recompute " +
            "global playback timing, validate active audio assignments " +
            "and reject unsupported cross-boundary state without data loss");
}
