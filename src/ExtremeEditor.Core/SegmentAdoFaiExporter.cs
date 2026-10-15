using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// A published, production-loader-verified .adofai candidate. No stock-game
/// playability assertion is implied by an in-process loader roundtrip.
/// </summary>
public sealed record SegmentAdoFaiExportResult(
    string DestinationPath,
    int FloorCount,
    int ActionCount);

/// <summary>
/// Safe, explicit export of a validated edited Segment composition.
/// Requires the original .adofai file to preserve fields that LevelDocument
/// cannot model, and publishes only after a complete temporary read-back.
/// </summary>
public static class SegmentAdoFaiExporter
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true
    };

    public static SegmentAdoFaiExportResult ExportValidated(
        LevelDocument sourceTemplate,
        SegmentTimeTimeline allocations,
        IReadOnlyDictionary<string, LevelSegment> editedSegments,
        string destinationPath,
        bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(editedSegments);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (string.IsNullOrWhiteSpace(sourceTemplate.SourcePath) ||
            sourceTemplate.SourcePath == "<synthetic>")
            throw new InvalidOperationException(
                "Validated .adofai export requires a real source file containing the raw JSON.");

        string source = Path.GetFullPath(sourceTemplate.SourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(source), ".adofai",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destination), ".adofai",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Both source and output must be .adofai files.");
        if (SamePath(source, destination))
            throw new InvalidOperationException(
                "Exporting over the original source chart is forbidden.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Raw source .adofai file is required.", source);

        RejectLinkedFilesAndDirectories(source);
        RejectLinkedFilesAndDirectories(destination);
        if (Directory.Exists(destination))
            throw new IOException("The output path already names a directory.");
        if (!overwriteExisting && File.Exists(destination))
            throw new IOException("The output already exists; explicit overwrite consent is required.");

        // This is a new chart, not a roundtrip expected to equal the source.
        // Revalidate every time to prevent stale allocation / edited data
        // being exported using an earlier preview's success flag.
        SegmentEditedCompositionResult composition =
            SegmentEditedCompositionValidator.ComposeAndValidate(
                sourceTemplate, allocations, editedSegments);
        if (!composition.Accepted || composition.Document is null ||
            composition.Timing is null)
        {
            string details = string.Join("; ", composition.Issues.Select(issue =>
                $"{issue.Kind} ({issue.SegmentId ?? "<global>"} floor {issue.Floor}): {issue.Detail}"));
            throw new InvalidOperationException(
                "The edited Segment composition cannot be exported: " + details);
        }

        LevelDocument candidate = composition.Document;
        JsonObject sourceRoot = ParseRoot(source);
        JsonObject serialized = CreateDocument(sourceRoot, candidate);

        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent))
            throw new ArgumentException("An absolute destination directory is required.", nameof(destinationPath));

        // Only the unique temporary filename is owned by this operation.
        // A failed write/reload never opens, truncates or replaces the user's
        // previous output, nor the original source chart.
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent,
            ".extremeeditor-segment-export-" + Guid.NewGuid().ToString("N") + ".adofai.tmp");
        try
        {
            using (FileStream stream = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, serialized, OutputOptions);
                stream.Flush(flushToDisk: true);
            }

            LevelDocument reopened = AdoFaiLoader.Load(temporary).Document;
            VerifyReadBack(candidate, composition.Timing, reopened);
            VerifyRawReadBack(serialized, ParseRoot(temporary));

            // Recheck immediately before publication. File.Move without
            // overwrite never clobbers a concurrently created destination.
            if (SamePath(source, destination))
                throw new InvalidOperationException("Cannot publish to source.");
            RejectLinkedFilesAndDirectories(destination);
            if (!overwriteExisting && File.Exists(destination))
                throw new IOException("The output was created during export; refusing to overwrite.");

            File.Move(temporary, destination, overwrite: overwriteExisting);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new SegmentAdoFaiExportResult(
            destination, candidate.FloorCount, candidate.ActionStore.ActionCount);
    }

    private static JsonObject CreateDocument(JsonObject source, LevelDocument chart)
    {
        var root = (JsonObject)source.DeepClone();
        JsonObject settings = root["settings"] is JsonObject previous
            ? (JsonObject)previous.DeepClone()
            : new JsonObject();

        settings["bpm"] = chart.InitialBpm;
        settings["songFilename"] = chart.SongFilename;
        settings["volume"] = chart.SongVolumePercent;
        settings["offset"] = chart.OffsetMilliseconds;
        settings["pitch"] = chart.PitchPercent;
        settings["countdownTicks"] = chart.CountdownTicks;
        settings["separateCountdownTime"] = chart.SeparateCountdownTime;
        settings["hitsound"] = chart.DefaultHitSound;
        settings["hitsoundVolume"] = chart.HitSoundVolumePercent;
        root["settings"] = settings;

        var angles = new JsonArray();
        foreach (double angle in chart.Angles)
            angles.Add(angle);
        root["angleData"] = angles;
        root.Remove("pathData");

        JsonArray sourceActions = source["actions"] as JsonArray ?? new JsonArray();
        var writtenActions = new JsonArray();
        foreach (LevelAction action in chart.ActionStore.Actions)
            writtenActions.Add(BuildAction(action, sourceActions));
        root["actions"] = writtenActions;

        var writtenDecorations = new JsonArray();
        foreach (LevelDecoration decoration in chart.Decorations)
        {
            if (decoration.Properties is null)
                throw new InvalidDataException("An exported decoration has no source properties.");
            var obj = new JsonObject
            {
                ["eventType"] = decoration.EventType
            };
            if (decoration.Floor is int floor)
                obj["floor"] = floor;
            foreach ((string name, JsonNode? value) in decoration.Properties)
            {
                if (name is "eventType" or "floor")
                    throw new InvalidDataException(
                        "Decoration payload must not override its typed floor/eventType.");
                obj[name] = value?.DeepClone();
            }
            writtenDecorations.Add(obj);
        }
        root["decorations"] = writtenDecorations;
        return root;
    }

    private static JsonObject BuildAction(LevelAction action, JsonArray sourceActions)
    {
        JsonObject? sourceAction = null;
        if (action.SourceIndex >= 0)
        {
            if (action.SourceIndex >= sourceActions.Count ||
                sourceActions[action.SourceIndex] is not JsonObject raw)
                throw new InvalidDataException(
                    $"Action source index {action.SourceIndex} is absent from the original .adofai.");
            sourceAction = raw;
            if (!string.Equals(raw["eventType"]?.ToString(), action.EventType,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The event type at a reused source index no longer matches.");
        }

        // The original object protects unknown properties on known actions
        // (e.g. FutureEvent payload or futureSpeed on SetSpeed). Edited raw
        // event representations take precedence when provided.
        JsonObject data;
        if (action.PropertyOverrides is not null)
            data = (JsonObject)action.PropertyOverrides.DeepClone();
        else if (action.SourceProperties is not null)
            data = (JsonObject)action.SourceProperties.DeepClone();
        else if (sourceAction is not null)
            data = (JsonObject)sourceAction.DeepClone();
        else if (action.Kind != LevelActionKind.Unknown)
            data = new JsonObject();
        else
            throw new InvalidDataException(
                $"New unknown event '{action.EventType}' has no lossless source JSON.");

        // An event with a raw representation must preserve its unknown
        // properties; overlaying the edited JSON on the source also preserves
        // fields omitted from an overrides object.
        if (sourceAction is not null)
        {
            JsonObject full = (JsonObject)sourceAction.DeepClone();
            MergeRaw(full, data);
            data = full;
        }

        data["floor"] = action.Floor;
        data["eventType"] = action.EventType;
        data["active"] = action.Active;
        SetOrRemove(data, "speedType", action.SpeedType);
        SetOrRemove(data, "beatsPerMinute", action.BeatsPerMinute);
        SetOrRemove(data, "bpmMultiplier", action.BpmMultiplier);
        SetOrRemove(data, "icon", action.CustomIcon);
        SetOrRemove(data, "hitsound", action.HitSound);
        SetOrRemove(data, "hitsoundVolume", action.HitSoundVolumePercent);
        SetOrRemove(data, "gameSound", action.GameSound);
        SetOrRemove(data, "planets", action.Planets);
        SetOrRemove(data, "angleOffset", action.AngleOffset);
        SetOrRemove(data, "duration", action.Duration);
        SetOrRemove(data, "tag", action.TargetTag);
        SetOrRemove(data, "eventTag", action.EventTag);
        return data;
    }

    private static void MergeRaw(JsonObject into, JsonObject updates)
    {
        foreach ((string name, JsonNode? value) in updates)
            into[name] = value?.DeepClone();
    }

    private static void SetOrRemove(JsonObject obj, string name, string? value)
    {
        if (value is not null)
            obj[name] = value;
        else
            obj.Remove(name);
    }

    private static void SetOrRemove(JsonObject obj, string name, double? value)
    {
        if (value is double number)
            obj[name] = number;
        else
            obj.Remove(name);
    }

    private static JsonObject ParseRoot(string path)
    {
        byte[] utf8 = AdoFaiJson.NormalizeToUtf8(File.ReadAllBytes(path));
        return JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject
        ?? throw new InvalidDataException("ADOFAI root must be a JSON object.");
    }

    private static void VerifyReadBack(
        LevelDocument expected, TimingMap expectedTiming, LevelDocument actual)
    {
        if (expected.FloorCount != actual.FloorCount ||
            !expected.Angles.SequenceEqual(actual.Angles) ||
            expected.ActionStore.ActionCount != actual.ActionStore.ActionCount ||
            expected.DecorationCount != actual.DecorationCount ||
            !Near(expected.InitialBpm, actual.InitialBpm) ||
            !Near(expected.OffsetMilliseconds, actual.OffsetMilliseconds) ||
            !Near(expected.PitchPercent, actual.PitchPercent) ||
            expected.CountdownTicks != actual.CountdownTicks ||
            expected.SeparateCountdownTime != actual.SeparateCountdownTime ||
            !string.Equals(expected.SongFilename, actual.SongFilename,
                StringComparison.Ordinal) ||
            !Near(expected.SongVolumePercent, actual.SongVolumePercent) ||
            !Near(expected.HitSoundVolumePercent, actual.HitSoundVolumePercent) ||
            !string.Equals(expected.DefaultHitSound, actual.DefaultHitSound,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Read-back mismatch in chart dimensions, global settings or event counts.");

        LevelAction[] intended = expected.ActionStore.Actions.ToArray();
        LevelAction[] parsed = actual.ActionStore.Actions.ToArray();
        for (int i = 0; i < intended.Length; i++)
        {
            LevelAction a = intended[i];
            LevelAction b = parsed[i];
            if (a.Floor != b.Floor ||
                !string.Equals(a.EventType, b.EventType, StringComparison.Ordinal) ||
                a.Kind != b.Kind ||
                a.Active != b.Active ||
                !string.Equals(a.SpeedType, b.SpeedType, StringComparison.Ordinal) ||
                !NullableNumberEqual(a.BeatsPerMinute, b.BeatsPerMinute) ||
                !NullableNumberEqual(a.BpmMultiplier, b.BpmMultiplier) ||
                !string.Equals(a.CustomIcon, b.CustomIcon, StringComparison.Ordinal) ||
                !NullableNumberEqual(a.Duration, b.Duration) ||
                !NullableNumberEqual(a.AngleOffset, b.AngleOffset) ||
                !string.Equals(a.TargetTag, b.TargetTag, StringComparison.Ordinal) ||
                !string.Equals(a.EventTag, b.EventTag, StringComparison.Ordinal) ||
                !string.Equals(a.HitSound, b.HitSound, StringComparison.Ordinal) ||
                !NullableNumberEqual(a.HitSoundVolumePercent, b.HitSoundVolumePercent) ||
                !string.Equals(a.GameSound, b.GameSound, StringComparison.Ordinal) ||
                !string.Equals(a.Planets, b.Planets, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Read-back changed modeled event at ordered index {i}, floor {a.Floor}.");
        }

        for (int i = 0; i < expected.DecorationCount; i++)
        {
            LevelDecoration a = expected.Decorations[i];
            LevelDecoration b = actual.Decorations[i];
            if (a.Floor != b.Floor ||
                !string.Equals(a.EventType, b.EventType, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(a.Properties, b.Properties))
                throw new InvalidDataException(
                    $"Read-back changed decoration at index {i}.");
        }

        for (int floor = 0; floor < expected.FloorCount; floor++)
        {
            if (!NearVector(expected.Positions[floor], actual.Positions[floor]))
                throw new InvalidDataException(
                    $"Read-back changed modeled geometry at floor {floor}.");
        }

        TimingMap actualTiming = TimingMapBuilder.Build(actual);
        if (expectedTiming.Floors.Count != actualTiming.Floors.Count)
            throw new InvalidDataException("Read-back changed timing floor count.");
        for (int i = 0; i < expectedTiming.Floors.Count; i++)
        {
            FloorTiming a = expectedTiming.Floors[i];
            FloorTiming b = actualTiming.Floors[i];
            if (!Near(a.EntryTime, b.EntryTime) ||
                !Near(a.ExitTime, b.ExitTime) ||
                !Near(a.Bpm, b.Bpm) ||
                !Near(a.PauseSeconds, b.PauseSeconds) ||
                !Near(a.AngleMoved, b.AngleMoved) ||
                a.IsCcw != b.IsCcw || a.MidSpin != b.MidSpin)
                throw new InvalidDataException(
                    $"Read-back changed modeled timing at floor {i}.");
        }
    }

    private static void VerifyRawReadBack(JsonObject written, JsonObject readBack)
    {
        if (!JsonNode.DeepEquals(written, readBack))
            throw new InvalidDataException(
                "Read-back changed serialized raw ADOFAI JSON properties.");
    }

    private static bool NullableNumberEqual(double? a, double? b) =>
        a.HasValue == b.HasValue &&
        (!a.HasValue || Near(a.Value, b!.Value));

    private static bool Near(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 1e-7;

    private static bool NearVector(Vector2 a, Vector2 b) =>
        float.IsFinite(a.X) && float.IsFinite(a.Y) &&
        float.IsFinite(b.X) && float.IsFinite(b.Y) &&
        Vector2.DistanceSquared(a, b) <= 1e-6f;

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void RejectLinkedFilesAndDirectories(string path)
    {
        for (string? current = path; current is not null;
             current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(
                    "ADOFAI export does not follow symlinks or directory junctions.");
        }
    }
}
