using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class SegmentSplitMergeRegression
{
    public static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"extremeeditor-segment-poc-{Guid.NewGuid():N}.adofai");

        try
        {
            // Real .adofai JSON through the production loader: midspin, speed,
            // event source metadata, multiple events at a floor, decorations.
            File.WriteAllText(path, """
            {
              "angleData": [0, 45, 999, 90, 270, 15],
              "settings": {
                "bpm": 150,
                "songFilename": "song.ogg",
                "offset": 123,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false
              },
              "actions": [
                { "floor": 0, "eventType": "SetSpeed", "speedType": "Bpm", "beatsPerMinute": 150 },
                {
                  "floor": 2,
                  "eventType": "MoveDecorations",
                  "tag": "fx",
                  "duration": 2,
                  "positionOffset": [3, 4],
                  "unknownPayload": {"future": [1, 2, 3]}
                },
                { "floor": 4, "eventType": "Twirl" },
                { "floor": 5, "eventType": "SetSpeed", "speedType": "Multiplier", "bpmMultiplier": 1.5 },
                {
                  "floor": 5,
                  "eventType": "CustomBackground",
                  "bgImage": "background.png",
                  "color": "001122",
                  "active": false
                }
              ],
              "decorations": [
                {
                  "floor": 2,
                  "eventType": "AddDecoration",
                  "decorationImage": "sprite.png",
                  "position": [1, 2],
                  "custom": {"nested": 42}
                },
                {"floor": 5, "eventType": "AddText", "text": "hello"},
                {"eventType": "AddDecoration", "decorationImage": "global.png"}
              ]
            }
            """);

            LevelDocument source = AdoFaiLoader.Load(path).Document;
            double[] originalAngles = source.Angles.ToArray();
            string originalMoveSource = source.ActionStore.Actions
                .Single(action => action.EventType == "MoveDecorations")
                .SourceProperties?.ToJsonString()
                ?? throw new InvalidOperationException("Fixture did not preserve MoveDecorations payload.");

            LevelSegment[] parts = LevelSegmentPartitioner.Split(source, 2, 5).ToArray();
            Equal(3, parts.Length, "segment count");
            AssertRange(parts[0], 0, 2, "A");
            AssertRange(parts[1], 2, 5, "B");
            AssertRange(parts[2], 5, 7, "C");

            // Each angle belongs to its destination floor. The second segment
            // must own the incoming angle into floor 2, not lose the edge at cut.
            Sequence([0d], parts[0].DestinationAngles, "A destination angles");
            Sequence([45d, 999d, 90d], parts[1].DestinationAngles, "B destination angles");
            Sequence([270d, 15d], parts[2].DestinationAngles, "C destination angles");
            Sequence(originalAngles, parts.SelectMany(part => part.DestinationAngles), "all angles");

            Check(parts[1].Actions.Any(action => action.Floor == 0 &&
                action.EventType == "MoveDecorations"), "B action local floor 0");
            Check(parts[1].Actions.Any(action => action.Floor == 2 &&
                action.EventType == "Twirl"), "B action local floor 2");
            Equal(2, parts[2].Actions.Count(action => action.Floor == 0),
                "C two source-ordered events at local floor 0");
            Check(parts[1].Decorations.Any(decoration => decoration.Floor == 0),
                "B decoration local floor 0");
            Check(parts[2].Decorations.Any(decoration => decoration.Floor == 0),
                "C decoration local floor 0");
            Check(parts.SelectMany(part => part.Decorations).All(decoration => decoration.Floor.HasValue),
                "global decorations must stay with project/template, not be copied to every segment");

            LevelDocument merged = LevelSegmentPartitioner.Merge(source, parts);
            Sequence(originalAngles, merged.Angles, "roundtrip angleData including midspin");
            Equal(source.FloorCount, merged.FloorCount, "roundtrip floor count");
            Equal(source.InitialBpm, merged.InitialBpm, "roundtrip root BPM");
            Equal(source.OffsetMilliseconds, merged.OffsetMilliseconds, "roundtrip song offset");
            Equal(source.SongFilename, merged.SongFilename, "roundtrip song name");

            var originalActions = source.ActionStore.Actions.Select(ActionSignature).ToArray();
            var mergedActions = merged.ActionStore.Actions.Select(ActionSignature).ToArray();
            Sequence(originalActions, mergedActions, "action types, floors, ordering, source indices and payloads");

            var originalDecorations = source.Decorations.Select(DecorationSignature).ToArray();
            var mergedDecorations = merged.Decorations.Select(DecorationSignature).ToArray();
            Sequence(originalDecorations, mergedDecorations, "decorations including project-wide entries");

            TimingMap originalTiming = TimingMapBuilder.Build(source);
            TimingMap mergedTiming = TimingMapBuilder.Build(merged);
            for (int floor = 0; floor < source.FloorCount; floor++)
                Near(originalTiming.GetEntryTime(floor), mergedTiming.GetEntryTime(floor),
                    $"roundtrip timing at floor {floor}");

            // Partitioning is non-destructive. A collaborator mutating their
            // local event/decoration must not silently mutate the source or peer.
            LevelAction segmentMove = parts[1].Actions.Single(action =>
                action.EventType == "MoveDecorations");
            Check(!ReferenceEquals(
                segmentMove.SourceProperties,
                source.ActionStore.Actions.Single(action => action.EventType == "MoveDecorations").SourceProperties),
                "source event JSON must be deep-copied");
            segmentMove.SourceProperties!["tag"] = "edited-locally";
            Equal(originalMoveSource,
                source.ActionStore.Actions.Single(action => action.EventType == "MoveDecorations")
                    .SourceProperties!.ToJsonString(),
                "local mutation must not alter source");

            LevelDecoration segmentDecoration = parts[1].Decorations.Single();
            LevelDecoration sourceDecoration = source.Decorations.Single(decoration => decoration.Floor == 2);
            Check(!ReferenceEquals(segmentDecoration.Properties, sourceDecoration.Properties),
                "decoration JSON must be deep-copied");
            segmentDecoration.Properties["decorationImage"] = "changed.png";
            Equal("sprite.png", sourceDecoration.Properties["decorationImage"]?.ToString(),
                "local decoration mutation must not alter source");

            // Repartition with different cuts after joining; this is the
            // building block of moving a work assignment without moving notes.
            LevelSegment[] repartitioned = LevelSegmentPartitioner.Split(merged, 1, 4).ToArray();
            LevelDocument rejoined = LevelSegmentPartitioner.Merge(merged, repartitioned);
            Sequence(originalAngles, rejoined.Angles, "repartitioned angleData");
            Sequence(originalActions, rejoined.ActionStore.Actions.Select(ActionSignature),
                "repartitioned events");

            ExpectRejected(() => LevelSegmentPartitioner.Split(source, 2, 2),
                "duplicate cut");
            ExpectRejected(() => LevelSegmentPartitioner.Split(source, 8),
                "out-of-bounds cut");
            LevelSegment[] overlap = parts.ToArray();
            overlap[1] = overlap[1] with { StartFloor = 1 };
            ExpectRejected(() => LevelSegmentPartitioner.Merge(source, overlap),
                "overlapping segments");
            LevelSegment[] gap = parts.ToArray();
            gap[1] = gap[1] with { StartFloor = 3 };
            ExpectRejected(() => LevelSegmentPartitioner.Merge(source, gap),
                "missing coverage");

            // No Git engine, arbitrary-time cutting or semantic ADOFAI export in
            // this slice; those are later stages, not implied by this PASS.
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string ActionSignature(LevelAction a) => string.Join("|",
        a.Floor,
        a.EventType,
        a.Active,
        a.SourceIndex,
        a.SpeedType,
        a.BeatsPerMinute,
        a.BpmMultiplier,
        a.SpeedRatio,
        a.TargetTag,
        a.EventTag,
        a.SourceProperties?.ToJsonString(),
        a.PropertyOverrides?.ToJsonString());

    private static string DecorationSignature(LevelDecoration d) => string.Join("|",
        d.Floor?.ToString() ?? "<global>", d.EventType, d.SourceIndex, d.Properties.ToJsonString());

    private static void AssertRange(LevelSegment segment, int start, int end, string name)
    {
        Equal(start, segment.StartFloor, $"{name} start floor");
        Equal(end, segment.EndFloorExclusive, $"{name} end floor");
        Equal(end - start, segment.FloorCount, $"{name} floor count");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        T[] left = expected.ToArray();
        T[] right = actual.ToArray();
        if (!left.SequenceEqual(right))
            throw new InvalidOperationException(
                $"{label}: expected [{string.Join(", ", left)}], actual [{string.Join(", ", right)}]");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }

    private static void Near(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }

    private static void Check(bool valid, string label)
    {
        if (!valid)
            throw new InvalidOperationException(label);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"Invalid partition was accepted: {label}");
    }
}
