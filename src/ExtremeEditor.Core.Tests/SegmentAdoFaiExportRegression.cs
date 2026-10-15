using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Real .adofai source -> independently edited floor Segments -> verified
/// temporary export -> production AdoFaiLoader read-back. Isolated OS temp
/// files only; no WPF, native game, source repo Git, or global configuration.
/// </summary>
internal static class SegmentAdoFaiExportRegression
{
    private const int SampleRate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-segment-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunWithTemporaryFiles(root);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RunWithTemporaryFiles(string root)
    {
        string sourcePath = Path.Combine(root, "source.adofai");
        string destination = Path.Combine(root, "merged.adofai");
        File.WriteAllText(sourcePath, """
        {
          "angleData": [0, 45, 999, 90, 270, 15],
          "customRoot": { "preserve": [1, {"future": "untouched"}] },
          "settings": {
            "bpm": 120, "songFilename": "audio.ogg",
            "offset": 2000, "pitch": 100,
            "countdownTicks": 0, "separateCountdownTime": false,
            "volume": 80, "hitsound": "Kick", "hitsoundVolume": 75,
            "customSettings": { "sealed": ["a", "b"] }
          },
          "actions": [
            { "floor": 1, "eventType": "SetSpeed", "speedType": "Multiplier",
              "bpmMultiplier": 1.25, "futureSpeed": {"preserve": true} },
            { "floor": 2, "eventType": "Twirl" },
            { "floor": 3, "eventType": "Pause", "duration": 0.5 },
            { "floor": 3, "eventType": "CustomBackground", "active": false,
              "custom": {"deep": [1, {"revision": "original"}]} },
            { "floor": 4, "eventType": "FutureEvent", "futurePayload": {
                "nested": ["keep", {"id": 17}]
              } },
            { "floor": 5, "eventType": "SetSpeed", "speedType": "Bpm",
              "beatsPerMinute": 180 }
          ],
          "decorations": [
            { "floor": 3, "eventType": "AddDecoration",
              "decorationImage": "sprite.png",
              "extra": {"name": "original", "sequence": [1,2,3]} },
            { "eventType": "AddText", "text": "GLOBAL",
              "metadata": {"list": [3,2,1]} }
          ]
        }
        """);

        byte[] sourceBefore = File.ReadAllBytes(sourcePath);
        LevelDocument template = AdoFaiLoader.Load(sourcePath).Document;
        LevelSegment[] parts = LevelSegmentPartitioner.Split(template, 2, 5).ToArray();
        Check(parts.Length == 3 && template.FloorCount == 7,
            "fixture must have two floor-aligned cuts");

        var edited = CreateEdits(parts);
        LevelDocument merged = LevelSegmentPartitioner.Merge(
            template, [edited["A"], edited["B"], edited["C"]]);
        SegmentTimeTimeline timeline = Assignments(merged);
        SegmentEditedCompositionResult preview =
            SegmentEditedCompositionValidator.ComposeAndValidate(template, timeline, edited);
        Check(preview.Accepted && preview.Document is not null,
            "edited composition should already be GREEN");

        string payloadA = JsonSerializer.Serialize(edited["A"]);
        string payloadB = JsonSerializer.Serialize(edited["B"]);
        string payloadC = JsonSerializer.Serialize(edited["C"]);
        string allocationBefore = JsonSerializer.Serialize(timeline);

        SegmentAdoFaiExportResult exported =
            SegmentAdoFaiExporter.ExportValidated(
                template, timeline, edited, destination);

        Equal(Path.GetFullPath(destination), Path.GetFullPath(exported.DestinationPath),
            "export reports the published path");
        Equal(7, exported.FloorCount, "floor count");
        Equal(7, exported.ActionCount, "authored event count includes new Twirl");
        Check(File.Exists(destination), "validated export published an .adofai file");
        Check(new FileInfo(destination).Length > 0, "nonempty export");

        LevelDocument reloaded = AdoFaiLoader.Load(destination).Document;
        Sequence(merged.Angles, reloaded.Angles, "edited angleData and 999 midspin");
        Equal(merged.FloorCount, reloaded.FloorCount, "floor count after read-back");
        Equal(merged.ActionStore.ActionCount, reloaded.ActionStore.ActionCount,
            "event count after read-back");
        Equal(merged.DecorationCount, reloaded.DecorationCount,
            "decoration count after read-back");
        Equal(merged.InitialBpm, reloaded.InitialBpm, "global BPM");
        Equal(merged.OffsetMilliseconds, reloaded.OffsetMilliseconds, "global offset");
        Equal(merged.PitchPercent, reloaded.PitchPercent, "global pitch");
        Equal(merged.CountdownTicks, reloaded.CountdownTicks, "countdown");
        Equal(merged.SeparateCountdownTime, reloaded.SeparateCountdownTime,
            "separate countdown setting");
        Equal(merged.SongFilename, reloaded.SongFilename, "song filename");

        foreach ((LevelAction expected, LevelAction actual) in
                 merged.ActionStore.Actions.Zip(reloaded.ActionStore.Actions))
        {
            Equal(expected.Floor, actual.Floor, "reloaded event floor");
            Equal(expected.EventType, actual.EventType, "reloaded event type");
            Equal(expected.Active, actual.Active, "reloaded event activation");
            Equal(expected.BpmMultiplier, actual.BpmMultiplier, "reloaded multiplier");
            Equal(expected.BeatsPerMinute, actual.BeatsPerMinute, "reloaded BPM");
            Equal(expected.Duration, actual.Duration, "reloaded Pause duration");
        }

        TimingMap expectedMap = TimingMapBuilder.Build(merged);
        TimingMap actualMap = TimingMapBuilder.Build(reloaded);
        for (int floor = 0; floor < merged.FloorCount; floor++)
        {
            Near(expectedMap.GetEntryTime(floor), actualMap.GetEntryTime(floor),
                "reloaded entry time at floor " + floor);
            Near(expectedMap.Floors[floor].ExitTime, actualMap.Floors[floor].ExitTime,
                "reloaded exit time at floor " + floor);
            Equal(expectedMap.Floors[floor].IsCcw, actualMap.Floors[floor].IsCcw,
                "Twirl state retained at floor " + floor);
        }

        JsonObject rootJson = ParseJson(destination);
        Equal("untouched",
            rootJson["customRoot"]?["preserve"]?[1]?["future"]?.ToString(),
            "unknown top-level metadata");
        Equal("b",
            rootJson["settings"]?["customSettings"]?["sealed"]?[1]?.ToString(),
            "unknown settings metadata");
        JsonArray actions = rootJson["actions"]!.AsArray();
        Check(actions.Count == 7, "raw action array includes appended Twirl");
        JsonObject knownSpeed = FindAction(actions, "SetSpeed", 1);
        Equal("1.5", knownSpeed["bpmMultiplier"]?.ToString(),
            "typed edited multiplier overrides old source JSON");
        Equal("true", knownSpeed["futureSpeed"]?["preserve"]?.ToString()
            .ToLowerInvariant(), "unknown field on known event survives");
        JsonObject custom = FindAction(actions, "CustomBackground", 3);
        Equal("edited",
            custom["custom"]?["deep"]?[1]?["revision"]?.ToString(),
            "edited unknown nested payload survives");
        JsonObject future = FindAction(actions, "FutureEvent", 4);
        Equal("17", future["futurePayload"]?["nested"]?[1]?["id"]?.ToString(),
            "unsupported future event retains source JSON through SourceIndex");
        Check(actions.Any(node =>
            node?["floor"]?.ToString() == "5" &&
            node?["eventType"]?.ToString() == "Twirl"),
            "newly authored event has correct global floor");
        JsonArray decorations = rootJson["decorations"]!.AsArray();
        Equal("B-edited", decorations[0]?["extra"]?["name"]?.ToString(),
            "edited decoration payload preserved");
        Equal("GLOBAL", decorations[1]?["text"]?.ToString(),
            "floorless template decoration preserved exactly once");

        Sequence(sourceBefore, File.ReadAllBytes(sourcePath),
            "original .adofai source must never be overwritten");
        Equal(payloadA, JsonSerializer.Serialize(edited["A"]),
            "A input was not mutated");
        Equal(payloadB, JsonSerializer.Serialize(edited["B"]),
            "B input was not mutated");
        Equal(payloadC, JsonSerializer.Serialize(edited["C"]),
            "C input was not mutated");
        Equal(allocationBefore, JsonSerializer.Serialize(timeline),
            "sample allocations were not mutated");

        byte[] destinationBefore = File.ReadAllBytes(destination);
        ExpectRejected(() =>
            SegmentAdoFaiExporter.ExportValidated(
                template, timeline, edited, destination),
            "existing file protected unless overwrite explicitly requested");
        Sequence(destinationBefore, File.ReadAllBytes(destination),
            "no-overwrite rejection preserves destination bytes");

        // The exporter must REVALIDATE the current composition, never trust
        // a previously issued Accepted flag or bypass sample ownership.
        var stale = timeline with
        {
            Partitions = timeline.Partitions.Select(p => p.SegmentId == "B"
                ? p with { StartSample = p.StartSample + 24 }
                : p).ToArray()
        };
        SegmentEditedCompositionResult invalid =
            SegmentEditedCompositionValidator.ComposeAndValidate(template, stale, edited);
        Check(!invalid.Accepted, "stale range is rejected before file export");
        ExpectRejected(() =>
            SegmentAdoFaiExporter.ExportValidated(
                template, stale, edited, destination, overwriteExisting: true),
            "invalid composition cannot replace an existing export");
        Sequence(destinationBefore, File.ReadAllBytes(destination),
            "validation rejection preserves existing export");

        // The source chart cannot be replaced, even with an explicit
        // overwrite flag; its untouched raw JSON is our export fidelity base.
        ExpectRejected(() =>
            SegmentAdoFaiExporter.ExportValidated(
                template, timeline, edited, sourcePath, overwriteExisting: true),
            "export must not write over its own source template");
        Sequence(sourceBefore, File.ReadAllBytes(sourcePath),
            "source protection survives overwrite attempt");

        // A brand-new unknown event with no raw representation must not be
        // silently serialized as only {floor,eventType}. Fail closed instead.
        var unrepresentable = edited.ToDictionary(kv => kv.Key, kv => kv.Value,
            StringComparer.Ordinal);
        LevelSegment c = unrepresentable["C"];
        unrepresentable["C"] = c with
        {
            Actions = c.Actions.Append(
                new LevelAction(1, "UnrepresentedFutureEffect", true,
                    null, null, null, null)).ToArray()
        };
        Check(SegmentEditedCompositionValidator.ComposeAndValidate(
                template, timeline, unrepresentable).Accepted,
            "unknown zero-duration typed action is not a cross-boundary timing issue");
        ExpectRejected(() =>
            SegmentAdoFaiExporter.ExportValidated(
                template, timeline, unrepresentable, destination,
                overwriteExisting: true),
            "new unknown action without a lossless raw payload must be refused");
        Sequence(destinationBefore, File.ReadAllBytes(destination),
            "unrepresentable event cannot truncate or modify destination");

        SegmentAdoFaiExportResult overwritten =
            SegmentAdoFaiExporter.ExportValidated(
                template, timeline, edited, destination, overwriteExisting: true);
        Equal(destination, overwritten.DestinationPath,
            "explicitly permitted replacement reports destination");
        Sequence(destinationBefore, File.ReadAllBytes(destination),
            "deterministic re-export preserves file bytes");

        Check(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly)
                .Count() == 0,
            "temporary exports are not left behind on success or validation failure");
        Sequence(sourceBefore, File.ReadAllBytes(sourcePath),
            "source unchanged after all rejection and overwrite scenarios");
    }

    private static Dictionary<string, LevelSegment> CreateEdits(LevelSegment[] parts)
    {
        LevelSegment a = parts[0];
        double[] aAngles = a.DestinationAngles.ToArray();
        aAngles[0] = 30d;
        LevelAction[] aActions = a.Actions.Select(action =>
            action.EventType == "SetSpeed"
                ? action with { BpmMultiplier = 1.5 }
                : action).ToArray();

        LevelSegment b = parts[1];
        double[] bAngles = b.DestinationAngles.ToArray();
        bAngles[0] = 90d;
        LevelAction[] bActions = b.Actions.Select(action =>
        {
            if (action.EventType == "Pause")
                return action with { Duration = 1.25 };
            if (action.EventType != "CustomBackground")
                return action;
            JsonObject raw = (JsonObject)action.SourceProperties!.DeepClone();
            raw["custom"]!["deep"]![1]!["revision"] = "edited";
            JsonObject overrides = (JsonObject)action.PropertyOverrides!.DeepClone();
            overrides["custom"]!["deep"]![1]!["revision"] = "edited";
            return action with
            {
                SourceProperties = raw,
                PropertyOverrides = overrides
            };
        }).ToArray();
        LevelDecoration[] bDecorations = b.Decorations.Select(d =>
        {
            var properties = (JsonObject)d.Properties.DeepClone();
            properties["extra"]!["name"] = "B-edited";
            return d with { Properties = properties };
        }).ToArray();

        LevelSegment c = parts[2];
        double[] cAngles = c.DestinationAngles.ToArray();
        cAngles[0] = 225d;
        LevelAction[] cActions = c.Actions.Append(
            new LevelAction(0, "Twirl", true,
                null, null, null, null)).ToArray();

        return new Dictionary<string, LevelSegment>(StringComparer.Ordinal)
        {
            ["A"] = a with { DestinationAngles = aAngles, Actions = aActions },
            ["B"] = b with
            {
                DestinationAngles = bAngles,
                Actions = bActions,
                Decorations = bDecorations
            },
            ["C"] = c with { DestinationAngles = cAngles, Actions = cActions }
        };
    }

    private static SegmentTimeTimeline Assignments(LevelDocument chart)
    {
        TimingMap timing = TimingMapBuilder.Build(chart);
        (string Id, int Start, int End)[] groups =
        [
            ("A", 0, 2), ("B", 2, 5), ("C", 5, chart.FloorCount)
        ];
        var parts = groups.Select(group =>
        {
            long start = ToSample(PlaybackClock.ChartToAudioTime(
                chart, timing.GetEntryTime(group.Start)));
            long end = ToSample(PlaybackClock.ChartToAudioTime(
                chart, group.End < chart.FloorCount
                    ? timing.GetEntryTime(group.End)
                    : timing.Duration));
            return new SegmentTimePartition(
                group.Id, start, end, group.Id + "-r2", start,
                "author-" + group.Id, SegmentPartitionStatus.Active);
        }).ToArray();
        return new SegmentTimeTimeline(SampleRate, parts);
    }

    private static long ToSample(double time) =>
        checked((long)Math.Round(time * SampleRate,
            MidpointRounding.AwayFromZero));

    private static JsonObject ParseJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidOperationException("Export is not a JSON object.");

    private static JsonObject FindAction(JsonArray actions, string type, int floor) =>
        actions.OfType<JsonObject>().Single(action =>
            action["eventType"]?.ToString() == type &&
            action["floor"]?.ToString() == floor.ToString());

    private static void ExpectRejected(Action operation, string description)
    {
        try
        {
            operation();
        }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsafe export accepted: " + description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string description)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(description);
    }

    private static void Near(double expected, double actual, string description)
    {
        if (Math.Abs(expected - actual) > 1e-7)
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
