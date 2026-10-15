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
/// Only clean local main commits whose SHA matches the freshly fetched origin
/// main are eligible. A diverged/conflicted/unpublished HEAD is rejected;
/// neither a draft nor an older unpublished branch can be exported silently.
/// Fetch may advance the remote-tracking ref but no local branch is moved,
/// no implicit Pull/Push occurs, and user-authored content is preserved.
/// </summary>
public static class SegmentGitAdoFaiPipeline
{
    public static SegmentGitAdoFaiPublication ExportPublishedHead(
        LevelDocument sourceTemplate,
        SegmentGitWorkspace workspace,
        string destinationPath,
        bool overwriteExisting = false) =>
        throw new NotImplementedException(
            "RED: require clean published Git HEAD, read semantic merged snapshot, " +
            "validate floor/sample assignments, export and verify .adofai");
}
