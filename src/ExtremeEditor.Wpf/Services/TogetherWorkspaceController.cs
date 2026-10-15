using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

/// <summary>
/// A presentation-only row for an existing floor-aligned assignment.
/// Audio samples and floors are distinct coordinates; the UI must not infer
/// either from the other, or make parked revisions editable by accident.
/// </summary>
internal sealed record TogetherSegmentDisplay(
    string SegmentId,
    int StartFloor,
    int EndFloorExclusive,
    long StartSample,
    long EndSampleExclusive,
    string? OwnerId,
    SegmentPartitionStatus Status,
    string? ContentRevisionId,
    bool IsSelected,
    bool CanEdit);

internal sealed record TogetherWorkspaceDisplay(
    string? WorkspaceDirectory,
    string? CurrentCommit,
    string? SelectedSegmentId,
    IReadOnlyList<TogetherSegmentDisplay> Segments,
    bool HasStagedChanges,
    IReadOnlyList<SegmentTimeMergeConflict> TimelineConflicts,
    IReadOnlyList<string> ContentConflictSegmentIds);

/// <summary>
/// RED-only WPF editing boundary for the already tested Together Core.
/// Works with the real EditorSession in an isolated editor document, and
/// deliberately never treats a normal .adofai Save as a Git Commit.
///
/// A future WPF Together panel will call this controller from explicit user
/// actions. It must never auto-Pull over dirty editor state, save edits on
/// another owner's floors, or launch network/GUI automation.
/// </summary>
internal sealed class TogetherWorkspaceController
{
    public TogetherWorkspaceDisplay Display =>
        throw new NotImplementedException("RED: present workspace segments, owners and conflicts");

    public SegmentGitWorkspace Workspace =>
        throw new NotImplementedException("RED: expose the isolated collaboration workspace");

    public void Create(
        string workspaceDirectory,
        LevelDocument sourceTemplate,
        SegmentGitSnapshot initialSnapshot,
        string ownerId) =>
        throw new NotImplementedException(
            "RED: create local Together workspace from chart and floor-aligned allocation");

    public void Open(
        string workspaceDirectory,
        LevelDocument sourceTemplate,
        string ownerId) =>
        throw new NotImplementedException(
            "RED: open existing Together workspace without discarding user edits");

    public LevelDocument GetEditorDocument() =>
        throw new NotImplementedException(
            "RED: create independent editable full-chart view of workspace snapshot");

    public void SelectSegment(string segmentId) =>
        throw new NotImplementedException(
            "RED: select active segment belonging to current owner");

    public void StageSelected(EditorSession editor) =>
        throw new NotImplementedException(
            "RED: validate selected floor-only EditorSession edits, then stage safely");

    public string Commit(string message) =>
        throw new NotImplementedException("RED: explicit commit of staged Segment edits");

    public void ConnectLocalBare(string bareRepositoryPath) =>
        throw new NotImplementedException("RED: explicit local bare remote connection");

    public string Push() =>
        throw new NotImplementedException("RED: push only an explicit clean local commit");

    public string Fetch() =>
        throw new NotImplementedException("RED: fetch without overwriting local authored edits");

    public SegmentGitPullResult Pull(EditorSession? currentEditor) =>
        throw new NotImplementedException(
            "RED: reject dirty editor buffer, show semantic conflicts and preserve drafts");

    public SegmentGitAdoFaiPublication Export(
        string destinationPath,
        bool overwriteExisting = false) =>
        throw new NotImplementedException(
            "RED: publish clean synchronized Git HEAD through validated .adofai exporter");
}
