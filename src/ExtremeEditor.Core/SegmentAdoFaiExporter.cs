namespace ExtremeEditor.Core;

/// <summary>
/// A verified, published .adofai candidate. Validation and read-back must
/// complete before an existing destination can be replaced.
/// </summary>
public sealed record SegmentAdoFaiExportResult(
    string DestinationPath,
    int FloorCount,
    int ActionCount);

/// <summary>
/// RED-only export bridge for independently edited floor-aligned Segments.
/// ExportValidated MUST re-run SegmentEditedCompositionValidator itself and
/// never trust caller-supplied success claims. The template must originate
/// from a real on-disk .adofai file: its original JSON is required to retain
/// unknown root/settings/action fields missing from the typed LevelDocument.
///
/// Writes a validated temporary .adofai in the destination directory,
/// reopens it with AdoFaiLoader, compares the modeled chart and raw fields,
/// then atomically publishes to a distinct destination. Any failure preserves
/// the source, existing destination, and all user-authored Segment content.
/// No native ADOFAI playability guarantee is implied.
/// </summary>
public static class SegmentAdoFaiExporter
{
    public static SegmentAdoFaiExportResult ExportValidated(
        LevelDocument sourceTemplate,
        SegmentTimeTimeline allocations,
        IReadOnlyDictionary<string, LevelSegment> editedSegments,
        string destinationPath,
        bool overwriteExisting = false) =>
        throw new NotImplementedException(
            "RED: validate edited composition, serialize lossless supported " +
            ".adofai JSON, verify read-back, then publish without data loss");
}
