using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// Playback settings are supplied explicitly. Musical timing-point BPM is a
/// timeline guide, not necessarily the chart's starting playback tempo.
/// </summary>
public sealed record TimeFirstAdoFaiSettings(
    double InitialBpm, double OffsetMilliseconds = 0,
    double PitchPercent = 100, int CountdownTicks = 0,
    bool SeparateCountdownTime = false);

public sealed record TimeFirstAdoFaiCompositionResult(
    LevelDocument? Document, IReadOnlyList<string> Issues)
{
    public bool Accepted => Document is not null && Issues.Count == 0;
}

public sealed record TimeFirstAdoFaiExportResult(
    string DestinationPath, string SourceCommit, int FloorCount, int ActionCount);

/// <summary>
/// Conservative composition of a complete, sample-timed, newly authored chart.
/// Refuses incomplete intervals, unknown/cross-boundary events, mismatched
/// entry geometry and placements that the stock-like TimingMap cannot hit at
/// their declared integer song samples. Does not synthesize arbitrary speed
/// events or silently move authored tiles.
/// </summary>
public static class TimeFirstSegmentAdoFaiExporter
{
    private const double AngleToleranceDegrees = 0.00001;
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    public static TimeFirstAdoFaiCompositionResult ComposeAndValidate(
        TimeFirstSegmentSnapshot snapshot, TimeFirstAdoFaiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(snapshot);

        var issues = new List<string>();
        if (!double.IsFinite(settings.InitialBpm) || settings.InitialBpm <= 0 ||
            !double.IsFinite(settings.OffsetMilliseconds) ||
            !double.IsFinite(settings.PitchPercent) || settings.PitchPercent <= 0 ||
            settings.CountdownTicks < 0)
            return Rejected("Invalid explicit ADOFAI playback settings.");

        SegmentTimePartition[] active = snapshot.Timeline.Partitions
            .Where(static p => p.Status == SegmentPartitionStatus.Active)
            .OrderBy(static p => p.StartSample).ToArray();
        if (active.Length == 0)
            return Rejected("No active Segment is allocated.");
        long nextStart = 0;
        var allTiles = new List<(int Floor, string SegmentId, TimeFirstAuthoredTile Tile)>();
        var angles = new List<double>();
        var globalActions = new List<LevelAction>();
        var entries = new List<(string SegmentId, int GlobalFloor, double Angle)>();

        foreach (SegmentTimePartition partition in active)
        {
            if (partition.StartSample != nextStart)
                issues.Add($"Segment '{partition.SegmentId}' leaves an unassigned audio-time gap.");
            nextStart = partition.EndSampleExclusive;

            if (!snapshot.Content.TryGetValue(partition.SegmentId, out SegmentContentEntry? entry) ||
                entry.Kind != SegmentContentKind.TimeAuthored ||
                snapshot.Authored is null ||
                !snapshot.Authored.TryGetValue(partition.SegmentId, out TimeFirstAuthoredSegment? authored) ||
                authored.Tiles.Count == 0)
            {
                issues.Add($"Segment '{partition.SegmentId}' is empty or unfinished.");
                continue;
            }
            entries.Add((partition.SegmentId, angles.Count + 1, authored.EntryAngle));
            foreach (TimeFirstAuthoredTile tile in authored.Tiles)
            {
                int globalFloor = angles.Count + 1;
                allTiles.Add((globalFloor, partition.SegmentId, tile));
                angles.Add(tile.DestinationAngle);
                foreach (LevelAction action in tile.Actions)
                {
                    // All unsupported events fail closed. Global multi-tile,
                    // layout and decoration effects need a separate audit.
                    if (action.Kind is LevelActionKind.Unknown ||
                        action.EventType is "ScaleRadius" or "PositionTrack" or "MoveDecorations")
                    {
                        issues.Add($"Unverified effect '{action.EventType}' in Segment '{partition.SegmentId}'.");
                        continue;
                    }
                    if (action.SourceIndex >= 0 && action.SourceProperties is null &&
                        action.PropertyOverrides is null)
                    {
                        issues.Add($"Imported event '{action.EventType}' lacks its lossless source payload.");
                        continue;
                    }
                    globalActions.Add(action with
                    {
                        Floor = globalFloor,
                        SourceIndex = -1,
                        SourceProperties = action.SourceProperties is null ? null
                            : (JsonObject)action.SourceProperties.DeepClone(),
                        PropertyOverrides = action.PropertyOverrides is null ? null
                            : (JsonObject)action.PropertyOverrides.DeepClone()
                    });
                }
            }
        }
        if (nextStart != snapshot.Audio.TotalSamples)
            issues.Add("Active Segments do not cover the full measured song duration.");
        if (angles.Count == 0)
            issues.Add("A completed chart needs at least one authored destination floor.");
        if (issues.Count != 0)
            return new TimeFirstAdoFaiCompositionResult(null, issues);

        LevelDocument document = LevelDocument.CreateSynthetic(angles.Count + 1);
        document.Angles = angles.ToArray();
        document.RebuildGeometry();
        document.InitialBpm = settings.InitialBpm;
        document.OffsetMilliseconds = settings.OffsetMilliseconds;
        document.PitchPercent = settings.PitchPercent;
        document.CountdownTicks = settings.CountdownTicks;
        document.SeparateCountdownTime = settings.SeparateCountdownTime;
        document.SongFilename = snapshot.Audio.FileName;
        document.ReplaceActions(globalActions);

        TimingMap timing;
        try { timing = TimingMapBuilder.Build(document); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Rejected("Cannot evaluate the composed chart's modeled timing: " + ex.Message);
        }

        foreach ((string segmentId, int globalFloor, double requested) in entries)
        {
            double actualDegrees = NormalizeDegrees(
                timing.Floors[globalFloor].EntryAngle * (180.0 / Math.PI));
            if (AngularDistance(actualDegrees, NormalizeDegrees(requested)) >
                AngleToleranceDegrees)
                issues.Add($"Segment '{segmentId}' entry orientation differs from the composed ADOFAI geometry.");
        }
        // Half-open interval boundaries are sample integers, not generated
        // floor positions. Verify each actual floor's chart-to-audio transform.
        foreach ((int floor, string segmentId, TimeFirstAuthoredTile tile) in allTiles)
        {
            double audioSeconds = PlaybackClock.ChartToAudioTime(
                document, timing.GetEntryTime(floor));
            double sample = audioSeconds * snapshot.Audio.SampleRate;
            if (!double.IsFinite(sample) || Math.Abs(sample - tile.AudioSample) > 1.000001)
                issues.Add($"Segment '{segmentId}' tile '{tile.TileId}' has an audio-sample timing mismatch at floor {floor}.");
        }
        return issues.Count == 0
            ? new TimeFirstAdoFaiCompositionResult(document, [])
            : new TimeFirstAdoFaiCompositionResult(null, issues);
    }

    public static TimeFirstAdoFaiExportResult ExportValidated(
        TimeFirstSegmentGitWorkspace workspace, TimeFirstAdoFaiSettings settings,
        string destinationPath, bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".adofai",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Export must have the .adofai extension.", nameof(destinationPath));
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Export requires an absolute parent directory.");
        RejectLinkedAncestors(destination);
        if (Directory.Exists(destination))
            throw new IOException("Export destination is an existing directory.");
        if (!overwriteExisting && File.Exists(destination))
            throw new IOException("Export destination exists: overwrite consent is required.");
        // Keep the publication separate from the collaboration repository.
        string workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Directory));
        string prefix = workspaceRoot + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (destination.StartsWith(prefix, pathComparison) ||
            string.Equals(destination, workspaceRoot, pathComparison))
            throw new InvalidOperationException("Published charts must be outside the Git workspace.");

        workspace.AssertCleanOwnedWorktree();
        string commit = workspace.HeadCommit;
        TimeFirstSegmentSnapshot snapshot = workspace.ReadAt(commit);
        TimeFirstAdoFaiCompositionResult composition = ComposeAndValidate(snapshot, settings);
        if (!composition.Accepted)
            throw new InvalidOperationException(
                "The time-first chart is not safe to publish: " + string.Join("; ", composition.Issues));

        // A new time-first workspace has no old .adofai source template. The
        // measured audio identity must instead resolve beside the new chart.
        string audio = Path.GetFullPath(Path.Combine(directory, snapshot.Audio.FileName));
        RejectLinkedAncestors(audio);
        if (!File.Exists(audio))
            throw new FileNotFoundException("The chart's measured song file must be beside the output.", audio);
        using (FileStream stream = File.OpenRead(audio))
        {
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, snapshot.Audio.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output song file differs from the measured input audio.");
        }

        LevelDocument document = composition.Document!;
        JsonObject root = BuildChart(document);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            ".extremeeditor-time-export-" + Guid.NewGuid().ToString("N") + ".adofai.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, root, OutputOptions);
                stream.Flush(flushToDisk: true);
            }
            LevelDocument reopened = AdoFaiLoader.Load(temporary).Document;
            if (!document.Angles.SequenceEqual(reopened.Angles) ||
                document.FloorCount != reopened.FloorCount ||
                document.ActionStore.ActionCount != reopened.ActionStore.ActionCount ||
                document.SongFilename != reopened.SongFilename ||
                document.InitialBpm != reopened.InitialBpm ||
                document.OffsetMilliseconds != reopened.OffsetMilliseconds ||
                document.PitchPercent != reopened.PitchPercent ||
                document.CountdownTicks != reopened.CountdownTicks ||
                document.SeparateCountdownTime != reopened.SeparateCountdownTime)
                throw new InvalidDataException("ADOFAI loader readback changed the composed chart.");
            if (!JsonNode.DeepEquals(root, JsonNode.Parse(File.ReadAllText(temporary))))
                throw new InvalidDataException("Raw exported ADOFAI JSON changed during readback.");

            // A permissive parser may reload JSON but still change the typed
            // meaning of an action. Verify every supported action explicitly.
            foreach ((LevelAction expected, LevelAction actual) in
                     document.ActionStore.Actions.Zip(reopened.ActionStore.Actions))
            {
                if (expected.Floor != actual.Floor ||
                    expected.EventType != actual.EventType ||
                    expected.Active != actual.Active ||
                    expected.SpeedType != actual.SpeedType ||
                    expected.BeatsPerMinute != actual.BeatsPerMinute ||
                    expected.BpmMultiplier != actual.BpmMultiplier ||
                    expected.CustomIcon != actual.CustomIcon ||
                    expected.HitSound != actual.HitSound ||
                    expected.HitSoundVolumePercent != actual.HitSoundVolumePercent ||
                    expected.Planets != actual.Planets ||
                    expected.Duration != actual.Duration ||
                    expected.AngleOffset != actual.AngleOffset ||
                    expected.TargetTag != actual.TargetTag ||
                    expected.EventTag != actual.EventTag)
                    throw new InvalidDataException("Readback changed an authored action's typed meaning.");
            }
            TimingMap readbackTiming = TimingMapBuilder.Build(reopened);
            TimingMap expectedTiming = TimingMapBuilder.Build(document);
            for (int i = 0; i < reopened.FloorCount; i++)
            {
                if (Math.Abs(readbackTiming.Floors[i].EntryTime -
                             expectedTiming.Floors[i].EntryTime) > 1e-8 ||
                    Math.Abs(readbackTiming.Floors[i].ExitTime -
                             expectedTiming.Floors[i].ExitTime) > 1e-8 ||
                    readbackTiming.Floors[i].IsCcw != expectedTiming.Floors[i].IsCcw)
                    throw new InvalidDataException("ADOFAI readback changed floor timing or Twirl state.");
            }
            if (workspace.HeadCommit != commit)
                throw new InvalidOperationException("The committed chart advanced during export.");
            workspace.AssertCleanOwnedWorktree();
            RejectLinkedAncestors(destination);
            if (!overwriteExisting && File.Exists(destination))
                throw new IOException("An export destination appeared during publication.");
            File.Move(temporary, destination, overwrite: overwriteExisting);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new TimeFirstAdoFaiExportResult(destination, commit,
            document.FloorCount, document.ActionStore.ActionCount);
    }

    private static JsonObject BuildChart(LevelDocument document)
    {
        var angles = new JsonArray();
        foreach (double angle in document.Angles) angles.Add(angle);
        var actions = new JsonArray();
        foreach (LevelAction action in document.ActionStore.Actions)
        {
            JsonObject obj = action.SourceProperties is not null
                ? (JsonObject)action.SourceProperties.DeepClone()
                : new JsonObject();
            if (action.PropertyOverrides is not null)
                foreach ((string name, JsonNode? value) in action.PropertyOverrides)
                    obj[name] = value?.DeepClone();
            // Only modeled event kinds are allowed; don't discard unknown keys
            // from a full raw representation supplied by the author.
            if (action.Kind == LevelActionKind.Unknown)
                throw new InvalidDataException("Unmodeled event cannot be exported.");
            obj["floor"] = action.Floor;
            obj["eventType"] = action.EventType;
            obj["active"] = action.Active;
            Put(obj, "speedType", action.SpeedType);
            Put(obj, "beatsPerMinute", action.BeatsPerMinute);
            Put(obj, "bpmMultiplier", action.BpmMultiplier);
            Put(obj, "icon", action.CustomIcon);
            Put(obj, "hitsound", action.HitSound);
            Put(obj, "hitsoundVolume", action.HitSoundVolumePercent);
            Put(obj, "gameSound", action.GameSound);
            Put(obj, "planets", action.Planets);
            Put(obj, "angleOffset", action.AngleOffset);
            Put(obj, "duration", action.Duration);
            Put(obj, "tag", action.TargetTag);
            Put(obj, "eventTag", action.EventTag);
            actions.Add(obj);
        }
        return new JsonObject
        {
            ["angleData"] = angles,
            ["settings"] = new JsonObject
            {
                ["bpm"] = document.InitialBpm,
                ["songFilename"] = document.SongFilename,
                ["volume"] = document.SongVolumePercent,
                ["offset"] = document.OffsetMilliseconds,
                ["pitch"] = document.PitchPercent,
                ["countdownTicks"] = document.CountdownTicks,
                ["separateCountdownTime"] = document.SeparateCountdownTime,
                ["hitsound"] = document.DefaultHitSound,
                ["hitsoundVolume"] = document.HitSoundVolumePercent
            },
            ["actions"] = actions,
            ["decorations"] = new JsonArray()
        };
    }

    private static void Put(JsonObject obj, string key, string? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value;
    }
    private static void Put(JsonObject obj, string key, double? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value;
    }

    private static TimeFirstAdoFaiCompositionResult Rejected(string issue) =>
        new(null, [issue]);

    private static double NormalizeDegrees(double angle)
    {
        double value = angle % 360;
        return value < 0 ? value + 360 : value;
    }

    private static double AngularDistance(double a, double b)
    {
        double diff = Math.Abs(a - b) % 360;
        return Math.Min(diff, 360 - diff);
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Export cannot use symbolic-link or junction paths.");
        }
    }
}
