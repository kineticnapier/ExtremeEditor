using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Display projection of one independently owned floor-aligned Segment.
/// Start/EndSample are decoded-audio coordinates, NOT floor numbers.
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
/// Explicit, owner-scoped adapter between WPF's undoable EditorSession
/// and the separately versioned Segment Git workspace. In particular,
/// editing an .adofai document is never an implicit Git Commit.
/// </summary>
internal sealed class TogetherWorkspaceController
{
    private SegmentGitWorkspace? _workspace;
    private LevelDocument? _template;
    private string? _ownerId;
    private string? _selected;
    private bool _staged;
    private IReadOnlyList<SegmentTimeMergeConflict> _timelineConflicts =
        Array.Empty<SegmentTimeMergeConflict>();
    private IReadOnlyList<string> _contentConflicts = Array.Empty<string>();

    public SegmentGitWorkspace Workspace =>
        _workspace ?? throw new InvalidOperationException(
            "Create or open a Together workspace first.");

    public TogetherWorkspaceDisplay Display
    {
        get
        {
            if (_workspace is null)
                return new(null, null, null,
                    Array.Empty<TogetherSegmentDisplay>(), false,
                    _timelineConflicts, _contentConflicts);

            SegmentGitSnapshot current = _workspace.Load();
            var rows = current.Timeline.Partitions
                .OrderBy(p => p.StartSample)
                .ThenBy(p => p.SegmentId, StringComparer.Ordinal)
                .Select(partition =>
                {
                    current.Segments.TryGetValue(partition.SegmentId, out LevelSegment? segment);
                    bool writable = partition.Status == SegmentPartitionStatus.Active &&
                        string.Equals(partition.OwnerId, _ownerId, StringComparison.Ordinal) &&
                        segment is not null;
                    return new TogetherSegmentDisplay(
                        partition.SegmentId,
                        segment?.StartFloor ?? -1,
                        segment?.EndFloorExclusive ?? -1,
                        partition.StartSample,
                        partition.EndSampleExclusive,
                        partition.OwnerId,
                        partition.Status,
                        partition.ContentRevisionId,
                        string.Equals(partition.SegmentId, _selected, StringComparison.Ordinal),
                        writable);
                }).ToArray();

            return new(_workspace.Directory,
                ResolveHead(_workspace), _selected, rows, _staged,
                _timelineConflicts, _contentConflicts);
        }
    }

    public void Create(
        string workspaceDirectory,
        LevelDocument sourceTemplate,
        SegmentGitSnapshot initialSnapshot,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        CheckOwner(ownerId);
        if (sourceTemplate.SourcePath == "<synthetic>" ||
            !File.Exists(sourceTemplate.SourcePath))
            throw new InvalidOperationException("A real original .adofai chart must be open.");

        SegmentGitWorkspace.ValidateSnapshot(initialSnapshot);
        RequireComposition(sourceTemplate, initialSnapshot);
        SegmentGitWorkspace created = SegmentGitWorkspace.Initialize(workspaceDirectory);
        created.Save(initialSnapshot);
        created.Commit("Create Together workspace");
        Initialize(created, sourceTemplate, ownerId);
    }

    public void Open(
        string workspaceDirectory,
        LevelDocument sourceTemplate,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        CheckOwner(ownerId);
        if (sourceTemplate.SourcePath == "<synthetic>" ||
            !File.Exists(sourceTemplate.SourcePath))
            throw new InvalidOperationException("A real original .adofai chart must be open.");

        SegmentGitWorkspace opened = SegmentGitWorkspace.Open(workspaceDirectory);
        SegmentGitSnapshot snapshot = opened.Load();
        RequireComposition(sourceTemplate, snapshot);
        Initialize(opened, sourceTemplate, ownerId);
    }

    public LevelDocument GetEditorDocument()
    {
        SegmentGitSnapshot current = Workspace.Load();
        var active = ActiveContent(current);
        return LevelSegmentPartitioner.Merge(Template, active);
    }

    public void SelectSegment(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        SegmentGitSnapshot current = Workspace.Load();
        SegmentTimePartition allocation = current.Timeline.Partitions
            .SingleOrDefault(part =>
                string.Equals(part.SegmentId, segmentId, StringComparison.Ordinal))
            ?? throw new ArgumentException("No such Segment is assigned.", nameof(segmentId));
        if (allocation.Status != SegmentPartitionStatus.Active ||
            !string.Equals(allocation.OwnerId, _ownerId, StringComparison.Ordinal) ||
            !current.Segments.ContainsKey(segmentId))
            throw new InvalidOperationException(
                "Only an active Segment owned by this editor can be selected for editing.");
        _selected = segmentId;
    }

    public void StageSelected(EditorSession editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (_staged)
            throw new InvalidOperationException(
                "Commit existing staged Together edits before staging more.");
        if (!editor.IsDirty)
            throw new InvalidOperationException("There are no unsaved editor edits to stage.");
        if (editor.StructureEdits.Count != 0)
            throw new InvalidOperationException(
                "Floor insert/delete is not supported for a floor-aligned shared Segment.");
        Workspace.AssertCleanOwnedWorktree();

        string selectedId = _selected ??
            throw new InvalidOperationException("Select your own active Segment first.");
        SegmentGitSnapshot original = Workspace.Load();
        SegmentTimePartition selected = original.Timeline.Partitions
            .Single(part => part.SegmentId == selectedId);
        if (selected.Status != SegmentPartitionStatus.Active ||
            !string.Equals(selected.OwnerId, _ownerId, StringComparison.Ordinal))
            throw new InvalidOperationException("Selected Segment ownership has changed.");

        LevelDocument baseline = LevelSegmentPartitioner.Merge(
            Template, ActiveContent(original));
        LevelDocument edited = editor.Document;
        if (edited.FloorCount != baseline.FloorCount ||
            edited.Angles.Length != baseline.Angles.Length ||
            !string.Equals(edited.SourcePath, baseline.SourcePath, StringComparison.Ordinal) ||
            !GlobalSettingsEqual(baseline, edited) ||
            !SerializedEqual(
                baseline.Decorations.Where(d => d.Floor is null).ToArray(),
                edited.Decorations.Where(d => d.Floor is null).ToArray()))
            throw new InvalidOperationException(
                "Together cannot stage floor-count changes, global settings or project-wide decorations.");

        int[] cuts = original.Segments.Values
            .Where(s => original.Timeline.Partitions.Any(p =>
                p.Status == SegmentPartitionStatus.Active &&
                p.SegmentId == FindSegmentId(original, s)))
            .Select(s => s.StartFloor)
            .Where(start => start > 0)
            .OrderBy(start => start).ToArray();
        LevelSegment[] sliced = LevelSegmentPartitioner.Split(edited, cuts);
        var byStart = sliced.ToDictionary(s => s.StartFloor);
        var content = original.Segments.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        foreach (SegmentTimePartition part in original.Timeline.Partitions
            .Where(p => p.Status == SegmentPartitionStatus.Active))
        {
            if (!content.TryGetValue(part.SegmentId, out LevelSegment? oldPart) ||
                !byStart.TryGetValue(oldPart.StartFloor, out LevelSegment? newPart) ||
                oldPart.EndFloorExclusive != newPart.EndFloorExclusive)
                throw new InvalidOperationException("Edited floor coverage no longer matches ownership.");

            if (string.Equals(part.SegmentId, selectedId, StringComparison.Ordinal))
                content[part.SegmentId] = newPart;
            else if (!SerializedEqual(oldPart, newPart))
                throw new InvalidOperationException(
                    $"Editor changed Segment '{part.SegmentId}' owned by another author.");
        }

        if (SerializedEqual(original.Segments[selectedId], content[selectedId]))
            throw new InvalidOperationException("The selected Segment has no new changes.");

        // Check the edited whole chart before writing anything to Git-owned
        // files. Stale sample allocations are NEVER silently retimed here.
        var proposal = new SegmentGitSnapshot(original.Timeline, content);
        SegmentGitWorkspace.ValidateSnapshot(proposal);
        RequireComposition(Template, proposal);

        Workspace.Save(proposal);
        _staged = true;
        ClearConflicts();
    }

    public string Commit(string message)
    {
        if (!_staged)
            throw new InvalidOperationException("There are no staged Together edits to commit.");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A commit message is required.", nameof(message));
        string revision = Workspace.Commit(message);
        _staged = false;
        return revision;
    }

    public void ConnectLocalBare(string bareRepositoryPath) =>
        SegmentGitRemoteSync.ConnectLocalBare(Workspace, bareRepositoryPath);

    public string Push()
    {
        if (_staged)
            throw new InvalidOperationException("Commit staged changes before Push.");
        Workspace.AssertCleanOwnedWorktree();
        return SegmentGitRemoteSync.Push(Workspace);
    }

    public string Fetch() => SegmentGitRemoteSync.Fetch(Workspace);

    public SegmentGitPullResult Pull(EditorSession? currentEditor)
    {
        if (_staged || currentEditor?.IsDirty == true)
            throw new InvalidOperationException(
                "Commit and clear unsaved authoring edits before Pull.");
        Workspace.AssertCleanOwnedWorktree();
        SegmentGitPullResult result = SegmentGitRemoteSync.Pull(Workspace);
        _timelineConflicts = result.TimelineConflicts.ToArray();
        _contentConflicts = result.ContentConflictSegmentIds.ToArray();
        if (result.Success)
        {
            if (_selected is not null)
            {
                SegmentGitSnapshot updated = Workspace.Load();
                SegmentTimePartition? selection = updated.Timeline.Partitions.FirstOrDefault(
                    p => p.SegmentId == _selected && p.Status == SegmentPartitionStatus.Active &&
                         p.OwnerId == _ownerId);
                if (selection is null) _selected = null;
            }
            ClearConflicts();
        }
        return result;
    }

    public SegmentGitAdoFaiPublication Export(
        string destinationPath,
        bool overwriteExisting = false)
    {
        if (_staged)
            throw new InvalidOperationException("Commit staged Together edits before exporting.");
        return SegmentGitAdoFaiPipeline.ExportPublishedHead(
            Template, Workspace, destinationPath, overwriteExisting);
    }

    private void Initialize(SegmentGitWorkspace workspace, LevelDocument template, string owner)
    {
        _workspace = workspace;
        _template = template;
        _ownerId = owner;
        _selected = null;
        _staged = false;
        ClearConflicts();
    }

    private LevelDocument Template => _template ??
        throw new InvalidOperationException("No original .adofai source is associated with this workspace.");

    private static LevelSegment[] ActiveContent(SegmentGitSnapshot snapshot)
    {
        var active = snapshot.Timeline.Partitions
            .Where(p => p.Status == SegmentPartitionStatus.Active)
            .OrderBy(p => p.StartSample)
            .ToArray();
        var segments = new List<LevelSegment>(active.Length);
        foreach (SegmentTimePartition item in active)
        {
            if (!snapshot.Segments.TryGetValue(item.SegmentId, out LevelSegment? segment))
                throw new InvalidOperationException(
                    "Missing active content: " + item.SegmentId);
            segments.Add(segment);
        }
        return segments.ToArray();
    }

    private static string? FindSegmentId(SegmentGitSnapshot snapshot, LevelSegment candidate)
    {
        foreach ((string id, LevelSegment segment) in snapshot.Segments)
            if (ReferenceEquals(segment, candidate))
                return id;
        return null;
    }

    private static void RequireComposition(
        LevelDocument source, SegmentGitSnapshot snapshot)
    {
        SegmentEditedCompositionResult result =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                source, snapshot.Timeline, snapshot.Segments);
        if (!result.Accepted)
            throw new InvalidOperationException(
                "Together chart/ownership validation failed: " +
                string.Join("; ", result.Issues.Select(i =>
                    i.Kind + " " + i.Detail)));
    }

    private static bool GlobalSettingsEqual(LevelDocument a, LevelDocument b) =>
        a.InitialBpm == b.InitialBpm &&
        a.SongFilename == b.SongFilename &&
        a.SongVolumePercent == b.SongVolumePercent &&
        a.OffsetMilliseconds == b.OffsetMilliseconds &&
        a.PitchPercent == b.PitchPercent &&
        a.CountdownTicks == b.CountdownTicks &&
        a.SeparateCountdownTime == b.SeparateCountdownTime &&
        a.DefaultHitSound == b.DefaultHitSound &&
        a.HitSoundVolumePercent == b.HitSoundVolumePercent;

    private static bool SerializedEqual<T>(T a, T b) =>
        string.Equals(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b),
            StringComparison.Ordinal);

    private void ClearConflicts()
    {
        _timelineConflicts = Array.Empty<SegmentTimeMergeConflict>();
        _contentConflicts = Array.Empty<string>();
    }

    private static void CheckOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("A nonempty Together owner ID is required.", nameof(owner));
    }

    private static string ResolveHead(SegmentGitWorkspace workspace) =>
        workspace.HeadCommit;
}
