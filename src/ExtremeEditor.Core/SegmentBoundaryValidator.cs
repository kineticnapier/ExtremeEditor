namespace ExtremeEditor.Core;

/// <summary>
/// Why a floor-aligned segment roundtrip cannot yet be certified as preserving
/// the reference chart's timing and resolved geometry at its boundaries.
/// A successful result is limited to the modeled timing/geometry semantics;
/// it does not certify a playable or lossless stock .adofai export.
/// </summary>
public enum SegmentBoundaryIssueKind : byte
{
    EventDrift,
    TimingDrift,
    GeometryDrift,
    PlaybackPoseDrift,
    SampleAnchorMismatch,
    UnsupportedCrossBoundaryEffect
}

public sealed record SegmentBoundaryIssue(
    SegmentBoundaryIssueKind Kind,
    int Floor,
    string Detail);

public sealed record SegmentBoundarySampleAnchor(
    int BoundaryFloor,
    int SampleRate,
    long EntrySample);

public sealed record SegmentBoundaryValidationResult(
    IReadOnlyList<SegmentBoundaryIssue> Issues)
{
    public bool CanExportModeledRoundTrip => Issues.Count == 0;
}

/// <summary>
/// RED-only verifier for the CURRENT floor-aligned Segment model.
/// The reference is an unmodified level, and segments must represent a
/// partition/roundtrip of that same level. Intentional chart edits need a
/// separate, future acceptance baseline and must not be "validated" here.
///
/// Audio-sample anchors are explicit *floor-entry* anchors, not arbitrary
/// time-cut assignments. A timeline partition can begin inside a floor:
/// such a cut is NOT certified by this API.
/// </summary>
public static class SegmentBoundaryValidator
{
    public static SegmentBoundaryValidationResult ValidateRoundTrip(
        LevelDocument reference,
        IReadOnlyList<LevelSegment> segments,
        IReadOnlyList<SegmentBoundarySampleAnchor>? sampleAnchors = null) =>
        throw new NotImplementedException(
            "RED: verify floor-boundary timing, world pose, content fidelity " +
            "and explicitly anchored audio samples before modeled export");
}
