using System.Text;
using System.Text.Json;

namespace ExtremeEditor.Core;

public sealed record TimeFirstAudioIdentity(
    string FileName,
    string Sha256,
    int SampleRate,
    long TotalSamples);

public sealed record TimeFirstSegmentSnapshot(
    TimeFirstAudioIdentity Audio,
    IReadOnlyList<MusicTimingPoint> TimingPoints,
    SegmentTimeTimeline Timeline,
    IReadOnlyDictionary<string, SegmentContentEntry> Content,
    IReadOnlyDictionary<string, SegmentTimeDraft> Drafts,
    IReadOnlyDictionary<string, TimeFirstAuthoredSegment>? Authored = null);

public sealed class TimeFirstSegmentContentMissingException : IOException
{
    public TimeFirstSegmentContentMissingException(string message) : base(message) { }
}

/// <summary>
/// Versioned Git persistence for sample-time-first Together projects. Its
/// owned files are disjoint from the legacy floor-aligned payload, so legacy
/// workspaces are never upgraded or rewritten implicitly.
/// </summary>
public sealed class TimeFirstSegmentGitWorkspace
{
    private const int SchemaVersion = 1;
    private const string Format = "extremeeditor-together-time-first";
    private const string MarkerName = "time-first-workspace.json";
    private const string ProjectName = "time-first-project.json";
    private const string SegmentDirectoryName = "time-first-segments";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record MarkerFile(int SchemaVersion, string Format);
    private sealed record ProjectFile(
        int SchemaVersion,
        TimeFirstAudioIdentity Audio,
        MusicTimingPoint[] TimingPoints,
        int SampleRate,
        SegmentTimePartition[] Partitions,
        string[] SegmentIds);
    private sealed record SegmentFile(
        int SchemaVersion,
        string SegmentId,
        SegmentContentEntry Content,
        SegmentTimeDraft Draft);

    private readonly SegmentGitWorkspace _git;

    internal SegmentGitWorkspace Transport => _git;

    private TimeFirstSegmentGitWorkspace(SegmentGitWorkspace git) => _git = git;

    public string Directory => _git.Directory;
    public string HeadCommit => _git.HeadCommit;
    public string CurrentBranch => _git.CurrentBranch;

    public static TimeFirstSegmentGitWorkspace Initialize(string directory)
    {
        SegmentGitWorkspace git = SegmentGitWorkspace.Initialize(directory);
        var workspace = new TimeFirstSegmentGitWorkspace(git);
        System.IO.Directory.CreateDirectory(workspace.OwnedPath(SegmentDirectoryName));
        workspace.WriteOwnedFile(MarkerName,
            Serialize(new MarkerFile(SchemaVersion, Format)));
        return workspace;
    }

    public static TimeFirstSegmentGitWorkspace Open(string directory)
    {
        SegmentGitWorkspace git = SegmentGitWorkspace.Open(directory);
        var workspace = new TimeFirstSegmentGitWorkspace(git);
        string marker = workspace.OwnedPath(MarkerName);
        if (!File.Exists(marker))
            throw new InvalidDataException(
                "This is a legacy floor-aligned Together workspace, not a time-first workspace.");
        workspace.VerifyMarker(File.ReadAllText(marker, Utf8));
        return workspace;
    }

    public void Save(TimeFirstSegmentSnapshot snapshot)
    {
        RequireWorkspace();
        ValidateSnapshot(snapshot);

        string[] ids = ActiveIds(snapshot.Timeline);
        var project = new ProjectFile(
            SchemaVersion,
            snapshot.Audio with { },
            snapshot.TimingPoints.Select(static point => point with { }).ToArray(),
            snapshot.Timeline.SampleRate,
            snapshot.Timeline.Partitions.Select(static partition => partition with { })
                .OrderBy(static partition => partition.StartSample)
                .ThenBy(static partition => partition.SegmentId, StringComparer.Ordinal)
                .ToArray(),
            ids);
        string projectJson = Serialize(project);
        var payloads = ids.ToDictionary(
            static id => id,
            id => Serialize(new SegmentFile(
                SchemaVersion, id, snapshot.Content[id] with { },
                FreezeDraft(snapshot.Drafts[id]))),
            StringComparer.Ordinal);

        string segmentDirectory = OwnedPath(SegmentDirectoryName);
        if (System.IO.Directory.Exists(segmentDirectory)) RejectLink(segmentDirectory);
        else System.IO.Directory.CreateDirectory(segmentDirectory);

        string projectPath = OwnedPath(ProjectName);
        string[] previousIds = File.Exists(projectPath)
            ? ReadProject(File.ReadAllText(projectPath, Utf8)).SegmentIds
            : [];
        foreach (string previousId in previousIds) ValidateSegmentId(previousId);
        foreach (string previousId in previousIds.Except(ids, StringComparer.Ordinal))
        {
            string obsolete = OwnedPath(SegmentRelativePath(previousId));
            if (File.Exists(obsolete))
            {
                RejectLink(obsolete);
                File.Delete(obsolete);
            }
        }

        foreach (string id in ids)
            WriteOwnedFile(SegmentRelativePath(id), payloads[id]);
        WriteOwnedFile(ProjectName, projectJson);
    }

    public TimeFirstSegmentSnapshot Load()
    {
        RequireWorkspace();
        string projectPath = OwnedPath(ProjectName);
        if (!File.Exists(projectPath))
            throw new InvalidDataException("The time-first project manifest has not been saved.");
        return ReadSnapshot(File.ReadAllText(projectPath, Utf8), id =>
        {
            string path = OwnedPath(SegmentRelativePath(id));
            if (!File.Exists(path))
                throw new TimeFirstSegmentContentMissingException(
                    $"Segment '{id}' is listed by the project but its payload file is missing.");
            return File.ReadAllText(path, Utf8);
        });
    }

    /// <summary>Read a committed snapshot without checking out its Git tree.</summary>
    public TimeFirstSegmentSnapshot ReadAt(string revision)
    {
        RequireWorkspace();
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 255 ||
            !revision.All(static c => char.IsAsciiLetterOrDigit(c) ||
                c is '/' or '_' or '-' or '.') ||
            revision.StartsWith('-') || revision.Contains("..", StringComparison.Ordinal) ||
            revision.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException("Unsafe time-first Git revision.", nameof(revision));

        string sha = _git.ExecuteGit(
            "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Git did not resolve a commit ID.");
        string FromTree(string path)
        {
            string treeLine = _git.ExecuteGit("ls-tree", sha, "--", path).TrimEnd();
            // Do not permit Git symlinks (120000), submodules or executable
            // payloads to masquerade as collaboration-owned JSON files.
            if (!treeLine.StartsWith("100644 blob ", StringComparison.Ordinal) ||
                !treeLine.EndsWith("\t" + path, StringComparison.Ordinal) ||
                treeLine.Split('\n').Length != 1)
                throw new InvalidDataException(
                    $"Committed time-first file '{path}' is missing or is not a regular JSON blob.");
            return _git.ExecuteGit("show", sha + ":" + path);
        }
        VerifyMarker(FromTree(MarkerName));
        return ReadSnapshot(FromTree(ProjectName),
            id => FromTree(SegmentRelativePath(id)));
    }

    public string Commit(string message)
    {
        RequireWorkspace();
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A commit message is required.", nameof(message));
        TimeFirstSegmentSnapshot snapshot = Load();

        var paths = new SortedSet<string>(StringComparer.Ordinal)
        {
            SegmentGitWorkspace.MarkerFileName, MarkerName, ProjectName
        };
        foreach (string id in snapshot.Drafts.Keys) paths.Add(SegmentRelativePath(id));
        string tracked = _git.ExecuteGit("ls-files", "-z", "--", SegmentDirectoryName);
        foreach (string path in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (IsOwnedSegmentPath(path)) paths.Add(path);

        string[] explicitPaths = paths.ToArray();
        _git.ExecuteGit(new[] { "add", "-A", "--" }.Concat(explicitPaths).ToArray());
        _git.ExecuteGit(new[]
        {
            "-c", "user.name=ExtremeEditor Together",
            "-c", "user.email=extremeeditor-together@localhost.invalid",
            "commit", "--only", "-m", message, "--"
        }.Concat(explicitPaths).ToArray());
        return _git.ExecuteGit("rev-parse", "--verify", "HEAD").Trim();
    }

    public void AssertCleanOwnedWorktree()
    {
        RequireWorkspace();
        string status = _git.ExecuteGit(
            "status", "--porcelain", "--untracked-files=all", "--",
            SegmentGitWorkspace.MarkerFileName, MarkerName, ProjectName, SegmentDirectoryName);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException(
                "Time-first collaboration data contains uncommitted changes.");
    }

    public static void ValidateSnapshot(TimeFirstSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Audio);
        ArgumentNullException.ThrowIfNull(snapshot.TimingPoints);
        ArgumentNullException.ThrowIfNull(snapshot.Timeline);
        ArgumentNullException.ThrowIfNull(snapshot.Content);
        ArgumentNullException.ThrowIfNull(snapshot.Drafts);

        TimeFirstAudioIdentity audio = snapshot.Audio;
        if (string.IsNullOrWhiteSpace(audio.FileName) ||
            !string.Equals(Path.GetFileName(audio.FileName), audio.FileName, StringComparison.Ordinal) ||
            audio.Sha256.Length != 64 || !audio.Sha256.All(char.IsAsciiHexDigit) ||
            audio.SampleRate <= 0 || audio.TotalSamples <= 0)
            throw new ArgumentException("The measured audio identity is invalid.", nameof(snapshot));

        SegmentTimePlanner.Validate(snapshot.Timeline);
        if (snapshot.Timeline.SampleRate != audio.SampleRate ||
            snapshot.Timeline.Partitions.Any(partition =>
                partition.EndSampleExclusive > audio.TotalSamples))
            throw new ArgumentException(
                "The sample timeline does not fit the measured audio identity.", nameof(snapshot));

        _ = MusicTimingMap.Build(audio.SampleRate, snapshot.TimingPoints);
        SegmentContentValidationResult contentValidation =
            SegmentContentPlanner.Validate(snapshot.Timeline, snapshot.Content);
        if (!contentValidation.Accepted)
            throw new ArgumentException(
                "The time-first content map is invalid: " +
                string.Join("; ", contentValidation.Issues.Select(issue =>
                    $"{issue.Kind} [{issue.SegmentId}]: {issue.Detail}")), nameof(snapshot));

        string[] activeIds = ActiveIds(snapshot.Timeline);
        if (!activeIds.SequenceEqual(snapshot.Drafts.Keys.OrderBy(
                static id => id, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException(
                "Every active Segment must have exactly one persisted draft.", nameof(snapshot));

        var emptyPartitions = new List<SegmentTimePartition>();
        var emptyDrafts = new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal);
        var authoredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in activeIds)
        {
            ValidateSegmentId(id);
            if (!snapshot.Content.TryGetValue(id, out SegmentContentEntry? content) ||
                content is null)
                throw new ArgumentException(
                    "Every active time-first Segment requires explicit content.", nameof(snapshot));
            SegmentTimePartition partition = snapshot.Timeline.Partitions.Single(
                item => item.SegmentId == id);
            SegmentTimeDraft draft = snapshot.Drafts[id];
            if (content.Kind == SegmentContentKind.Empty)
            {
                if (snapshot.Authored?.ContainsKey(id) == true)
                    throw new ArgumentException(
                        "An explicitly empty Segment cannot also have authored content.", nameof(snapshot));
                emptyPartitions.Add(partition);
                emptyDrafts.Add(id, draft);
            }
            else if (content.Kind == SegmentContentKind.TimeAuthored &&
                     content.TimeAuthoredContent is TimeFirstAuthoredSegment embedded)
            {
                if (snapshot.Authored is null ||
                    !snapshot.Authored.TryGetValue(id, out TimeFirstAuthoredSegment? authored))
                    throw new ArgumentException(
                        "Time-authored content is absent from the authored map.", nameof(snapshot));
                TimeFirstSegmentEditorSession.Validate(authored);
                if (!AuthoredEqual(embedded, authored) ||
                    !draft.TileAnchors.Select(anchor => (anchor.TileId, anchor.AudioSample))
                        .SequenceEqual(authored.Tiles.Select(tile =>
                            (tile.TileId, tile.AudioSample))))
                    throw new ArgumentException(
                        "Authored geometry and its persisted draft anchors differ.", nameof(snapshot));
                authoredIds.Add(id);
            }
            else
            {
                throw new ArgumentException(
                    "A time-first workspace cannot contain legacy floor-aligned content.", nameof(snapshot));
            }
        }
        if (snapshot.Authored is not null &&
            !authoredIds.SetEquals(snapshot.Authored.Keys))
            throw new ArgumentException(
                "The authored map contains an unknown or inactive Segment.", nameof(snapshot));
        if (emptyPartitions.Count != 0)
            _ = SegmentDraftTimeSearchIndex.Build(
                new SegmentTimeTimeline(snapshot.Timeline.SampleRate,
                    Array.AsReadOnly(emptyPartitions.ToArray())), emptyDrafts);

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in snapshot.Timeline.Partitions.Select(
                     static partition => partition.SegmentId))
        {
            ValidateSegmentId(id);
            if (!fileNames.Add(id))
                throw new ArgumentException(
                    "Segment IDs must be unique as file names.", nameof(snapshot));
        }
    }

    private TimeFirstSegmentSnapshot ReadSnapshot(
        string projectJson, Func<string, string> getSegment)
    {
        ProjectFile project = ReadProject(projectJson);
        var content = new Dictionary<string, SegmentContentEntry>(StringComparer.Ordinal);
        var drafts = new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal);
        var authored = new Dictionary<string, TimeFirstAuthoredSegment>(StringComparer.Ordinal);
        foreach (string id in project.SegmentIds)
        {
            ValidateSegmentId(id);
            if (content.ContainsKey(id))
                throw new InvalidDataException("Duplicate Segment ID in the time-first manifest.");
            SegmentFile file = Deserialize<SegmentFile>(getSegment(id));
            if (file.SchemaVersion != SchemaVersion ||
                !string.Equals(file.SegmentId, id, StringComparison.Ordinal) ||
                file.Content is null || file.Draft is null)
                throw new InvalidDataException(
                    $"Segment '{id}' payload does not match its manifest.");
            SegmentContentEntry entry = file.Content;
            if (entry.TimeAuthoredContent is TimeFirstAuthoredSegment timeAuthored)
            {
                TimeFirstAuthoredSegment frozen =
                    TimeFirstSegmentEditorSession.Freeze(timeAuthored);
                authored.Add(id, frozen);
                entry = entry with { TimeAuthoredContent = frozen };
            }
            content.Add(id, entry with { });
            drafts.Add(id, FreezeDraft(file.Draft));
        }

        var snapshot = new TimeFirstSegmentSnapshot(
            project.Audio with { },
            Array.AsReadOnly(project.TimingPoints.Select(static point => point with { }).ToArray()),
            new SegmentTimeTimeline(project.SampleRate,
                Array.AsReadOnly(project.Partitions.Select(static partition =>
                    partition with { }).ToArray())),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, SegmentContentEntry>(content),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, SegmentTimeDraft>(drafts),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, TimeFirstAuthoredSegment>(authored));
        try { ValidateSnapshot(snapshot); }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The saved time-first project is invalid.", ex);
        }
        return snapshot;
    }

    private static ProjectFile ReadProject(string json)
    {
        ProjectFile project = Deserialize<ProjectFile>(json);
        if (project.SchemaVersion != SchemaVersion || project.Audio is null ||
            project.TimingPoints is null || project.Partitions is null ||
            project.SegmentIds is null)
            throw new InvalidDataException("Unsupported time-first project schema.");
        foreach (string id in project.SegmentIds) ValidateSegmentId(id);
        return project;
    }

    private static bool AuthoredEqual(
        TimeFirstAuthoredSegment left, TimeFirstAuthoredSegment right)
    {
        if (left.SegmentId != right.SegmentId || left.OwnerId != right.OwnerId ||
            left.SampleRate != right.SampleRate || left.StartSample != right.StartSample ||
            left.EndSampleExclusive != right.EndSampleExclusive ||
            left.RevisionId != right.RevisionId || left.EntryAngle != right.EntryAngle ||
            left.Tiles.Count != right.Tiles.Count)
            return false;
        for (int i = 0; i < left.Tiles.Count; i++)
        {
            TimeFirstAuthoredTile a = left.Tiles[i];
            TimeFirstAuthoredTile b = right.Tiles[i];
            if (a.TileId != b.TileId || a.AudioSample != b.AudioSample ||
                a.DestinationAngle != b.DestinationAngle ||
                JsonSerializer.Serialize(a.Actions) != JsonSerializer.Serialize(b.Actions))
                return false;
        }
        return true;
    }

    private void RequireWorkspace()
    {
        string marker = OwnedPath(MarkerName);
        if (!File.Exists(marker))
            throw new InvalidDataException("Missing time-first workspace marker.");
        VerifyMarker(File.ReadAllText(marker, Utf8));
        _ = _git.CurrentBranch;
    }

    private static string[] ActiveIds(SegmentTimeTimeline timeline) =>
        timeline.Partitions
            .Where(static partition => partition.Status == SegmentPartitionStatus.Active)
            .Select(static partition => partition.SegmentId)
            .OrderBy(static id => id, StringComparer.Ordinal).ToArray();

    private void VerifyMarker(string json)
    {
        MarkerFile marker = Deserialize<MarkerFile>(json);
        if (marker.SchemaVersion != SchemaVersion ||
            !string.Equals(marker.Format, Format, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported time-first workspace schema.");
    }

    private string OwnedPath(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(
            Directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(Directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Owned path escapes the workspace.");
        if (File.Exists(path) || System.IO.Directory.Exists(path)) RejectLink(path);
        return path;
    }

    private void WriteOwnedFile(string relative, string content) =>
        File.WriteAllText(OwnedPath(relative), content, Utf8);

    private static SegmentTimeDraft FreezeDraft(SegmentTimeDraft draft) =>
        draft with
        {
            TileAnchors = Array.AsReadOnly(draft.TileAnchors.Select(static anchor =>
                new SegmentDraftTileAnchor(anchor.TileId, anchor.AudioSample)).ToArray())
        };

    private static string SegmentRelativePath(string id)
    {
        ValidateSegmentId(id);
        return SegmentDirectoryName + "/" + id + ".json";
    }

    private static bool IsOwnedSegmentPath(string path)
    {
        const string prefix = SegmentDirectoryName + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            !path.EndsWith(".json", StringComparison.Ordinal)) return false;
        return IsValidSegmentId(path[prefix.Length..^5]);
    }

    private static bool IsValidSegmentId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 128 &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateSegmentId(string? id)
    {
        if (!IsValidSegmentId(id))
            throw new ArgumentException(
                "Segment ID must be a safe ASCII file identifier.", nameof(id));
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Symbolic links and junctions are not allowed in collaboration paths.");
    }

    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Saved time-first JSON is empty or invalid.");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
