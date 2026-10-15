namespace ExtremeEditor.Core;

/// <summary>
/// A versioned snapshot of time allocations and the independently editable
/// floor-aligned content they reference. The dictionary key is a stable
/// SegmentId, not the segment's ordinal position or owner.
/// </summary>
public sealed record SegmentGitSnapshot(
    SegmentTimeTimeline Timeline,
    IReadOnlyDictionary<string, LevelSegment> Segments);

/// <summary>
/// RED-only contract for a dedicated, local Git-backed collaboration workspace.
/// The directory must be new/empty; it must never initialize or reconfigure the
/// ExtremeEditor source repository. Network push/pull and Git merge are NOT part
/// of this slice; the existing semantic three-way merger remains independent.
/// </summary>
public sealed class SegmentGitWorkspace
{
    private SegmentGitWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public static SegmentGitWorkspace Initialize(string directory) =>
        throw new NotImplementedException("RED: initialize isolated segment Git workspace");

    public static SegmentGitWorkspace Open(string directory) =>
        throw new NotImplementedException("RED: open existing segment Git workspace");

    /// <summary>Persist a deterministic schema-versioned snapshot in the worktree.</summary>
    public void Save(SegmentGitSnapshot snapshot) =>
        throw new NotImplementedException("RED: save segment content and time assignments");

    /// <summary>Load the working-tree snapshot without modifying any file.</summary>
    public SegmentGitSnapshot Load() =>
        throw new NotImplementedException("RED: load working-tree segment snapshot");

    /// <summary>Create a local Git commit of collaboration data only; return full SHA.</summary>
    public string Commit(string message) =>
        throw new NotImplementedException("RED: commit a segment workspace snapshot");

    public string CurrentBranch =>
        throw new NotImplementedException("RED: read workspace branch");

    public void CreateBranch(string branchName) =>
        throw new NotImplementedException("RED: create a Git branch without checkout");

    /// <summary>Reject dirty changes rather than silently discarding or stashing them.</summary>
    public void Checkout(string branchName) =>
        throw new NotImplementedException("RED: checkout existing branch safely");

    /// <summary>Read a committed snapshot by exact ref without changing HEAD/worktree.</summary>
    public SegmentGitSnapshot ReadAt(string revision) =>
        throw new NotImplementedException("RED: read independent immutable snapshot at a Git ref");
}
