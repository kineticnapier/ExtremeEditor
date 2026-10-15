namespace ExtremeEditor.Core;

/// <summary>
/// A verified .adofai file exported from a pinned, clean collaboration Git
/// HEAD. This attests to an in-process roundtrip, not stock-game parity.
/// </summary>
public sealed record SegmentGitAdoFaiPublication(
    string SourceCommit,
    string DestinationPath,
    int FloorCount,
    int ActionCount);

/// <summary>
/// RED-only bridge between an actual SegmentGitWorkspace revision and
/// SegmentAdoFaiExporter. The existing source .adofai template stays external
/// to the collaboration repository, and is never overwritten.
///
/// Only committed, clean workspace content is eligible; no draft content is
/// silently exported, no branch/remote refs are moved, and no implicit Push
/// occurs. Caller explicitly handles conflicts before requesting publication.
/// </summary>
public static class SegmentGitAdoFaiPipeline
{
    public static SegmentGitAdoFaiPublication ExportCommittedHead(
        LevelDocument sourceTemplate,
        SegmentGitWorkspace workspace,
        string destinationPath,
        bool overwriteExisting = false) =>
        throw new NotImplementedException(
            "RED: pin clean Git HEAD, read semantic merged snapshot, " +
            "validate floor/sample assignments, export and verify .adofai");
}
