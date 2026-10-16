using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ExtremeEditor.Core;

/// <summary>
/// A versioned snapshot of time allocations and independently editable
/// floor-aligned content. The key is a stable SegmentId, not owner or order.
/// </summary>
public sealed record SegmentGitSnapshot(
    SegmentTimeTimeline Timeline,
    IReadOnlyDictionary<string, LevelSegment> Segments);

/// <summary>
/// An isolated local Git repository for collaborative chart data.
/// This PoC deliberately does not push/pull, run Git merge, or export .adofai.
/// </summary>
public sealed class SegmentGitWorkspace
{
    private const int SchemaVersion = 1;
    private const string MarkerName = "workspace.json";
    private const string TimelineName = "timeline.json";
    private const string SegmentDirectoryName = "segments";
    private const string MarkerFormat = "extremeeditor-together-segments";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed record WorkspaceFile(int SchemaVersion, string Format);
    private sealed record TimelineFile(
        int SchemaVersion, int SampleRate,
        SegmentTimePartition[] Partitions, string[] SegmentIds);
    private sealed record SegmentFile(
        int SchemaVersion, string SegmentId, LevelSegment Segment);

    private SegmentGitWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public static SegmentGitWorkspace Initialize(string directory)
    {
        string root = NormalizeDirectory(directory);
        EnsureOutsideOtherRepositories(root);
        if (System.IO.Directory.Exists(root))
        {
            RejectLink(root);
            if (System.IO.Directory.EnumerateFileSystemEntries(root).Any())
                throw new ArgumentException("The collaboration directory must be empty.", nameof(directory));
        }
        else if (File.Exists(root))
            throw new ArgumentException("Workspace path is an existing file.", nameof(directory));

        System.IO.Directory.CreateDirectory(root);
        var workspace = new SegmentGitWorkspace(root);
        try
        {
            workspace.RunGit("init", "--initial-branch=main");
            System.IO.Directory.CreateDirectory(Path.Combine(root, SegmentDirectoryName));
            workspace.WriteOwnedFile(MarkerName,
                Serialize(new WorkspaceFile(SchemaVersion, MarkerFormat)));
        }
        catch
        {
            // Never delete or reset a directory here: failed initializations
            // leave inspectable data instead of risking user content.
            throw;
        }
        return workspace;
    }

    public static SegmentGitWorkspace Open(string directory)
    {
        string root = NormalizeDirectory(directory);
        if (!System.IO.Directory.Exists(root))
            throw new ArgumentException("Collaboration directory does not exist.", nameof(directory));
        RejectLink(root);
        var workspace = new SegmentGitWorkspace(root);
        workspace.RequireWorkspace();
        return workspace;
    }

    public void Save(SegmentGitSnapshot snapshot)
    {
        RequireWorkspace();
        ValidateSnapshot(snapshot);

        // Build every payload and validate every ID before touching any file.
        var ids = snapshot.Segments.Keys.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var timelineFile = new TimelineFile(
            SchemaVersion,
            snapshot.Timeline.SampleRate,
            snapshot.Timeline.Partitions.OrderBy(static p => p.StartSample)
                .ThenBy(static p => p.SegmentId, StringComparer.Ordinal).ToArray(),
            ids);
        string timelineJson = Serialize(timelineFile);
        var payloads = ids.ToDictionary(
            static id => id,
            id => Serialize(new SegmentFile(SchemaVersion, id, snapshot.Segments[id])),
            StringComparer.Ordinal);

        string segmentDir = Path.Combine(Directory, SegmentDirectoryName);
        if (System.IO.Directory.Exists(segmentDir))
            RejectLink(segmentDir);
        else
            System.IO.Directory.CreateDirectory(segmentDir);

        // Only remove segment files previously listed in our own manifest;
        // never delete unrelated files, even inside segments/.
        string timelinePath = OwnedPath(TimelineName);
        string[] previousIds = File.Exists(timelinePath)
            ? ReadTimeline(File.ReadAllText(timelinePath, Utf8)).SegmentIds
            : [];
        foreach (string previousId in previousIds)
            ValidateSegmentId(previousId);
        foreach (string previousId in previousIds.Except(ids, StringComparer.Ordinal))
        {
            string obsoletePath = OwnedPath(SegmentRelativePath(previousId));
            if (File.Exists(obsoletePath))
            {
                RejectLink(obsoletePath);
                File.Delete(obsoletePath);
            }
        }

        foreach (string id in ids)
            WriteOwnedFile(SegmentRelativePath(id), payloads[id]);
        WriteOwnedFile(TimelineName, timelineJson);
    }

    public SegmentGitSnapshot Load()
    {
        RequireWorkspace();
        string json = File.ReadAllText(OwnedPath(TimelineName), Utf8);
        return ReadSnapshot(json, id =>
            File.ReadAllText(OwnedPath(SegmentRelativePath(id)), Utf8));
    }

    public string Commit(string message)
    {
        RequireWorkspace();
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A commit message is required.", nameof(message));

        SegmentGitSnapshot snapshot = Load();
        var paths = new SortedSet<string>(StringComparer.Ordinal)
        {
            MarkerName,
            TimelineName
        };
        foreach (string id in snapshot.Segments.Keys)
            paths.Add(SegmentRelativePath(id));

        // Include previously tracked segment files, so removing one from the
        // manifest commits its deletion. Ignore all unrelated paths.
        string tracked = RunGit("ls-files", "-z", "--", SegmentDirectoryName);
        foreach (string path in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsOwnedSegmentPath(path))
                paths.Add(path);
        }

        string[] explicitPaths = paths.ToArray();
        RunGit(new[] { "add", "-A", "--" }.Concat(explicitPaths));
        // --only prevents pre-staged unrelated files from joining this commit.
        // Git identity is local to this invocation, never configured globally.
        RunGit(new[]
        {
            "-c", "user.name=ExtremeEditor Together",
            "-c", "user.email=extremeeditor-together@localhost.invalid",
            "commit", "--only", "-m", message, "--"
        }.Concat(explicitPaths));
        return RunGit("rev-parse", "--verify", "HEAD").Trim();
    }

    public string CurrentBranch
    {
        get
        {
            RequireWorkspace();
            return RunGit("symbolic-ref", "--quiet", "--short", "HEAD").Trim();
        }
    }

    /// <summary>
    /// Returns the immutable commit ID of this collaboration checkout.
    /// Does not expose general-purpose Git command execution to the UI.
    /// </summary>
    public string HeadCommit
    {
        get
        {
            RequireWorkspace();
            string sha = RunGit(
                "rev-parse", "--verify", "--end-of-options", "HEAD^{commit}").Trim();
            if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
                throw new InvalidOperationException("Invalid collaboration HEAD commit.");
            return sha;
        }
    }

    public void CreateBranch(string branchName)
    {
        RequireWorkspace();
        ValidateBranchName(branchName);
        RunGit("branch", branchName);
    }

    public void Checkout(string branchName)
    {
        RequireWorkspace();
        ValidateBranchName(branchName);

        // Untracked files outside the collaboration schema are ignored (and
        // never overwritten by a branch with only schema-owned files).
        // Dirty owned files, including untracked segment drafts, are protected.
        string status = RunGit(
            "status", "--porcelain", "--untracked-files=all", "--",
            MarkerName, TimelineName, SegmentDirectoryName);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException(
                "The collaboration workspace has uncommitted changes. Commit them before switching branches.");

        RunGit("switch", branchName);
        // A branch must carry the same collaboration marker and schema.
        RequireWorkspace();
    }

    public SegmentGitSnapshot ReadAt(string revision)
    {
        RequireWorkspace();
        if (string.IsNullOrWhiteSpace(revision) ||
            revision.Length > 255 ||
            !revision.All(static c =>
                char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.')
            || revision.StartsWith('-') || revision.Contains("..", StringComparison.Ordinal)
            || revision.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsafe Git revision.", nameof(revision));
        }

        // Resolve to an immutable commit before reading any tree entry.
        string sha = RunGit(
            "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Git did not resolve a commit hash.");

        // git show reads blobs without changing HEAD, index or the worktree.
        string markerJson = RunGit("show", sha + ":" + MarkerName);
        VerifyMarker(markerJson);
        string timelineJson = RunGit("show", sha + ":" + TimelineName);
        return ReadSnapshot(timelineJson,
            id => RunGit("show", sha + ":" + SegmentRelativePath(id)));
    }

    private static SegmentGitSnapshot ReadSnapshot(
        string timelineJson, Func<string, string> getSegment)
    {
        TimelineFile file = ReadTimeline(timelineJson);
        var segments = new Dictionary<string, LevelSegment>(StringComparer.Ordinal);
        foreach (string id in file.SegmentIds)
        {
            ValidateSegmentId(id);
            if (segments.ContainsKey(id))
                throw new InvalidDataException("Duplicate segment ID in saved manifest.");
            SegmentFile stored = Deserialize<SegmentFile>(getSegment(id));
            if (stored.SchemaVersion != SchemaVersion ||
                !string.Equals(stored.SegmentId, id, StringComparison.Ordinal) ||
                stored.Segment is null)
                throw new InvalidDataException("Segment data does not match its manifest.");
            segments.Add(id, stored.Segment);
        }
        var snapshot = new SegmentGitSnapshot(
            new SegmentTimeTimeline(file.SampleRate, file.Partitions), segments);
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static TimelineFile ReadTimeline(string json)
    {
        TimelineFile file = Deserialize<TimelineFile>(json);
        if (file.SchemaVersion != SchemaVersion ||
            file.Partitions is null || file.SegmentIds is null)
            throw new InvalidDataException("Unsupported segment timeline schema.");
        foreach (string id in file.SegmentIds)
            ValidateSegmentId(id);
        return file;
    }

    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Saved collaboration JSON is empty or invalid.");

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static void ValidateSnapshot(SegmentGitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Timeline);
        ArgumentNullException.ThrowIfNull(snapshot.Segments);
        SegmentTimePlanner.Validate(snapshot.Timeline);

        var allocated = snapshot.Timeline.Partitions
            .Select(static p => p.SegmentId).ToHashSet(StringComparer.Ordinal);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, LevelSegment segment) in snapshot.Segments)
        {
            ValidateSegmentId(id);
            if (!allocated.Contains(id) || !filenames.Add(id))
                throw new ArgumentException(
                    "Every saved content ID must correspond uniquely to a timeline partition.", nameof(snapshot));
            if (segment is null || segment.StartFloor < 0 ||
                segment.EndFloorExclusive <= segment.StartFloor ||
                segment.DestinationAngles is null ||
                segment.DestinationAngles.Length !=
                    segment.FloorCount - (segment.StartFloor == 0 ? 1 : 0) ||
                segment.Actions is null || segment.Decorations is null ||
                segment.Actions.Any(a => a is null || a.Floor < 0 || a.Floor >= segment.FloorCount) ||
                segment.Decorations.Any(d => d is null || !d.Floor.HasValue ||
                    d.Floor.Value < 0 || d.Floor.Value >= segment.FloorCount ||
                    d.Properties is null))
            {
                throw new ArgumentException(
                    $"Segment '{id}' has an invalid local floor-aligned payload.", nameof(snapshot));
            }
        }
        foreach (string id in allocated)
            ValidateSegmentId(id);
    }

    private static bool IsOwnedSegmentPath(string path)
    {
        const string prefix = SegmentDirectoryName + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            !path.EndsWith(".json", StringComparison.Ordinal))
            return false;
        string id = path[prefix.Length..^5];
        return IsValidSegmentId(id);
    }

    private static string SegmentRelativePath(string id)
    {
        ValidateSegmentId(id);
        return SegmentDirectoryName + "/" + id + ".json";
    }

    private static bool IsValidSegmentId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 128 &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateSegmentId(string? id)
    {
        if (!IsValidSegmentId(id))
            throw new ArgumentException("Segment ID must be a safe ASCII file identifier.", nameof(id));
    }

    internal static void ValidateBranchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 ||
            !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Contains("//", StringComparison.Ordinal) ||
            name.Contains("@{", StringComparison.Ordinal) ||
            name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.'))
            throw new ArgumentException("Unsafe Git branch name.", nameof(name));
    }

    private string OwnedPath(string relative)
    {
        // Relative names come exclusively from validated fixed schema paths.
        string path = Path.GetFullPath(Path.Combine(
            Directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Owned path escapes workspace.");
        if (File.Exists(path) || System.IO.Directory.Exists(path))
            RejectLink(path);
        return path;
    }

    private void WriteOwnedFile(string relative, string content)
    {
        string path = OwnedPath(relative);
        File.WriteAllText(path, content, Utf8);
    }

    private void RequireWorkspace()
    {
        RejectLink(Directory);
        string gitFolder = Path.Combine(Directory, ".git");
        if (!System.IO.Directory.Exists(gitFolder))
            throw new InvalidOperationException("Not an isolated collaboration Git repository.");
        RejectLink(gitFolder);
        string marker = OwnedPath(MarkerName);
        if (!File.Exists(marker))
            throw new InvalidOperationException("Missing ExtremeEditor Together workspace marker.");
        VerifyMarker(File.ReadAllText(marker, Utf8));

        string top = RunGit("rev-parse", "--show-toplevel").Trim();
        if (!string.Equals(
                Path.GetFullPath(top).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Git repository root does not match the workspace.");
    }

    private static void VerifyMarker(string json)
    {
        WorkspaceFile marker = Deserialize<WorkspaceFile>(json);
        if (marker.SchemaVersion != SchemaVersion || marker.Format != MarkerFormat)
            throw new InvalidDataException("This is not a supported collaboration Git repository.");
    }

    private static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Workspace directory is required.", nameof(directory));
        string full = Path.GetFullPath(directory);
        if (Path.GetPathRoot(full) == full)
            throw new ArgumentException("A filesystem root cannot be a collaboration workspace.", nameof(directory));
        return full;
    }

    private static void EnsureOutsideOtherRepositories(string root)
    {
        // Do not initialize a nested collaboration Git repo inside a source
        // checkout: it would modify the developer's normal working tree.
        var parent = System.IO.Directory.GetParent(root);
        while (parent is not null)
        {
            if (File.Exists(Path.Combine(parent.FullName, ".git")) ||
                System.IO.Directory.Exists(Path.Combine(parent.FullName, ".git")))
                throw new ArgumentException("Cannot initialize inside another Git repository.");
            parent = parent.Parent;
        }
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Symbolic links and junctions are not allowed in collaboration paths.");
    }

    public void AssertCleanOwnedWorktree()
    {
        RequireWorkspace();
        string status = RunGit(
            "status", "--porcelain", "--untracked-files=all", "--",
            MarkerName, TimelineName, SegmentDirectoryName);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException(
                "Collaboration data contains uncommitted changes. Commit before merging.");
    }

    internal string ExecuteGit(params string[] args)
    {
        RequireWorkspace();
        return RunGit(args);
    }

    internal string ExecuteGitWithIndex(string indexFile, IEnumerable<string> args)
    {
        RequireWorkspace();
        string fullIndex = Path.GetFullPath(indexFile);
        string expectedParent = Path.Combine(Directory, ".git");
        if (!string.Equals(Path.GetDirectoryName(fullIndex), expectedParent,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(fullIndex).StartsWith("extremeeditor-merge-index-", StringComparison.Ordinal))
            throw new ArgumentException("Merge index must be an isolated temporary .git file.", nameof(indexFile));
        return RunGit(args, fullIndex);
    }

    internal static string MarkerFileName => MarkerName;
    internal static string TimelineFileName => TimelineName;
    internal static string SegmentsFolderName => SegmentDirectoryName;

    internal static string SegmentFileName(string id) => SegmentRelativePath(id);

    private string RunGit(params string[] args) => RunGit((IEnumerable<string>)args);

    private string RunGit(IEnumerable<string> args, string? indexFile = null)
    {
        // Disable ambient Git repository/index override variables and repo
        // hooks. No shell is used and all parameters are passed as distinct
        // argument list entries.
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (string environmentVariable in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_PREFIX",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS",
            "GIT_CONFIG_COUNT"
        })
            start.Environment.Remove(environmentVariable);
        if (indexFile is not null)
            start.Environment["GIT_INDEX_FILE"] = indexFile;

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=" +
            Path.Combine(Directory, ".git", "extremeeditor-disabled-hooks"));
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.autocrlf=false");
        foreach (string argument in args)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Git executable.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Git command timed out.");
        }
        string errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Git exited {process.ExitCode}: {errors.Trim()}");
        return stdout;
    }
}
