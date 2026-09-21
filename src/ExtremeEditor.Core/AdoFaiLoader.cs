using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ExtremeEditor.Core;

public sealed record LoadMetrics(
    TimeSpan Read,
    TimeSpan Parse,
    TimeSpan BuildPath,
    long FileBytes);

public sealed record LoadResult(LevelDocument Document, LoadMetrics Metrics);

public static partial class AdoFaiLoader
{
    private static readonly JsonDocumentOptions TolerantJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static LoadResult Load(string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] bytes = File.ReadAllBytes(path);
        TimeSpan read = sw.Elapsed;

        sw.Restart();
        ReadOnlyMemory<byte> jsonBytes = AdoFaiJson.NormalizeToUtf8(bytes);
        using JsonDocument json = JsonDocument.Parse(jsonBytes, TolerantJsonOptions);

        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("angleData", out JsonElement angleData) ||
            angleData.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "This prototype currently requires angleData. Legacy pathData is not implemented.");
        }

        var angles = new double[angleData.GetArrayLength()];
        int ai = 0;
        foreach (JsonElement value in angleData.EnumerateArray())
        {
            angles[ai++] = TryReadDouble(value, out double parsed)
                ? parsed
                : throw new InvalidDataException(
                    $"Unsupported angleData value at index {ai - 1}: {value.ValueKind}");
        }

        double initialBpm = 100.0;
        string? songFilename = null;
        double offsetMilliseconds = 0.0;
        double pitchPercent = 100.0;
        int countdownTicks = 4;
        bool separateCountdownTime = false;
        string defaultHitSound = "Kick";
        double hitSoundVolumePercent = 100.0;
        if (root.TryGetProperty("settings", out JsonElement settings) &&
            settings.ValueKind == JsonValueKind.Object)
        {
            double? bpm = ReadLooseDoubleProperty(settings, "bpm");
            if (bpm is > 0) initialBpm = bpm.Value;

            songFilename = ReadLooseString(settings, "songFilename");
            // `offset` is the stock key. A few generators have emitted
            // `songOffset`, so accept it as a compatibility fallback.
            offsetMilliseconds = ReadLooseDoubleProperty(settings, "offset")
                                 ?? ReadLooseDoubleProperty(settings, "songOffset")
                                 ?? 0.0;
            pitchPercent = ReadLooseDoubleProperty(settings, "pitch") ?? 100.0;
            if (TryReadIntProperty(settings, "countdownTicks", out int ticks) && ticks >= 0)
                countdownTicks = ticks;
            if (settings.TryGetProperty("separateCountdownTime", out JsonElement separateCountdown))
                separateCountdownTime = ReadLooseBool(separateCountdown, defaultValue: false);

            defaultHitSound = ReadLooseString(settings, "hitsound") ?? "Kick";
            hitSoundVolumePercent = ReadLooseDoubleProperty(settings, "hitsoundVolume") ?? 100.0;
        }

        var actionTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var parsedActions = new List<LevelAction>();
        int actionCount = 0;
        if (root.TryGetProperty("actions", out JsonElement actions) &&
            actions.ValueKind == JsonValueKind.Array)
        {
            actionCount = actions.GetArrayLength();
            foreach (JsonElement action in actions.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object)
                    continue;

                string type = ReadLooseString(action, "eventType") ?? "<unknown>";
                actionTypes.TryGetValue(type, out int count);
                actionTypes[type] = count + 1;

                if (!TryReadIntProperty(action, "floor", out int floor))
                    continue;

                bool active = !action.TryGetProperty("active", out JsonElement activeValue) ||
                              ReadLooseBool(activeValue, defaultValue: true);
                parsedActions.Add(new LevelAction(
                    floor,
                    type,
                    active,
                    ReadLooseString(action, "speedType"),
                    ReadLooseDoubleProperty(action, "beatsPerMinute"),
                    ReadLooseDoubleProperty(action, "bpmMultiplier"),
                    ReadLooseString(action, "icon"))
                {
                    HitSound = ReadLooseString(action, "hitsound"),
                    HitSoundVolumePercent = ReadLooseDoubleProperty(action, "hitsoundVolume"),
                    GameSound = ReadLooseString(action, "gameSound"),
                    Planets = ReadLooseString(action, "planets"),
                    AngleOffset = ReadLooseDoubleProperty(action, "angleOffset"),
                    Duration = ReadLooseDoubleProperty(action, "duration")
                });
            }
        }

        ComputeSpeedRatios(parsedActions, initialBpm);
        IReadOnlyDictionary<int, LevelAction[]> actionsByFloor = parsedActions
            .GroupBy(action => action.Floor)
            .ToDictionary(group => group.Key, group => group.ToArray());

        TimeSpan parse = sw.Elapsed;

        sw.Restart();
        var positions = PathBuilder.BuildPositions(angles);
        var bounds = PathBuilder.CalculateBounds(positions);
        TimeSpan build = sw.Elapsed;

        var document = new LevelDocument
        {
            SourcePath = path,
            Angles = angles,
            Positions = positions,
            ActionCount = actionCount,
            ActionTypeCounts = actionTypes,
            ActionsByFloor = actionsByFloor,
            InitialBpm = initialBpm,
            SongFilename = songFilename,
            OffsetMilliseconds = offsetMilliseconds,
            PitchPercent = pitchPercent,
            CountdownTicks = countdownTicks,
            SeparateCountdownTime = separateCountdownTime,
            DefaultHitSound = defaultHitSound,
            HitSoundVolumePercent = hitSoundVolumePercent,
            Bounds = bounds
        };

        return new LoadResult(
            document,
            new LoadMetrics(read, parse, build, bytes.LongLength));
    }

    private static void ComputeSpeedRatios(List<LevelAction> actions, double initialBpm)
    {
        double bpm = initialBpm > 0 ? initialBpm : 100.0;
        foreach (LevelAction action in actions
                     .Where(action => action.Active && string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                     .OrderBy(action => action.Floor))
        {
            if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                action.BpmMultiplier is double multiplier && multiplier > 0)
            {
                action.SpeedRatio = multiplier;
                bpm *= multiplier;
            }
            else if (action.BeatsPerMinute is double targetBpm && targetBpm > 0)
            {
                action.SpeedRatio = bpm > 0 ? targetBpm / bpm : null;
                bpm = targetBpm;
            }
        }
    }

    private static string? ReadLooseString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    private static double? ReadLooseDoubleProperty(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && TryReadDouble(value, out double parsed)
            ? parsed
            : null;

    private static bool TryReadIntProperty(JsonElement parent, string property, out int result)
    {
        result = 0;
        if (!parent.TryGetProperty(property, out JsonElement value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result)) return true;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result)) return true;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool ReadLooseBool(JsonElement value, bool defaultValue)
    {
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        string text = value.ToString();
        if (bool.TryParse(text, out bool parsed)) return parsed;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return defaultValue;
    }
}

internal static class AdoFaiJson
{
    // ADOFAI files in the wild are not guaranteed to be pristine UTF-8 JSON.
    // Keep loose-format compatibility isolated here.
    public static ReadOnlyMemory<byte> NormalizeToUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsMemory(3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetBytes(text);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            string text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetBytes(text);
        }

        return bytes;
    }
}

public static class AdoFaiSaver
{
    public static void SaveAngles(LevelDocument document, string outputPath)
    {
        if (document.SourcePath == "<synthetic>" || !File.Exists(document.SourcePath))
            throw new InvalidOperationException("Synthetic levels cannot be saved by this prototype.");

        byte[] bytes = File.ReadAllBytes(document.SourcePath);
        ReadOnlyMemory<byte> jsonBytes = AdoFaiJson.NormalizeToUtf8(bytes);
        string jsonText = Encoding.UTF8.GetString(jsonBytes.Span);
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            jsonText,
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Root JSON value is not an object.");

        var angleData = new System.Text.Json.Nodes.JsonArray();
        foreach (double angle in document.Angles)
            angleData.Add(angle);
        node["angleData"] = angleData;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(outputPath, node.ToJsonString(options));
    }
}
