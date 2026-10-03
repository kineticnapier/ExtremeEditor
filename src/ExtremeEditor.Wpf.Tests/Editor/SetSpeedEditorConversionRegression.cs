using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SetSpeedEditorConversionRegression
{
    internal static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        level.InitialBpm = 100.0;

        var first = new LevelAction(1, "SetSpeed", true, "Multiplier", null, 2.0, null)
        {
            SourceIndex = 0
        };
        var target = new LevelAction(3, "SetSpeed", true, "Bpm", 100.0, 1.0, null)
        {
            SourceIndex = 1
        };
        level.ReplaceActions([first, target]);

        if (!SetSpeedEditorConversion.TryGetPreviousEffectiveBpm(level, target, out double previousBpm))
            throw new InvalidOperationException("SetSpeed previous effective BPM could not be resolved.");
        AssertNear(200.0, previousBpm, "previous effective BPM after earlier 2x SetSpeed");

        if (!SetSpeedEditorConversion.TryConvertBpmToMultiplier(200.0, 100.0, out double half))
            throw new InvalidOperationException("BPM -> multiplier conversion was rejected.");
        AssertNear(0.5, half, "200 BPM -> 100 BPM");

        if (!SetSpeedEditorConversion.TryConvertMultiplierToBpm(200.0, 0.5, out double hundred))
            throw new InvalidOperationException("Multiplier -> BPM conversion was rejected.");
        AssertNear(100.0, hundred, "200 BPM x 0.5");

        var currentBpm = new JsonObject
        {
            ["floor"] = 3,
            ["eventType"] = "SetSpeed",
            ["active"] = true,
            ["speedType"] = "Bpm",
            ["beatsPerMinute"] = 100.0,
            ["bpmMultiplier"] = 1.0,
            ["unknownPayload"] = "preserve-me"
        };
        JsonObject switchToMultiplier = (JsonObject)currentBpm.DeepClone();
        switchToMultiplier["speedType"] = "Multiplier";
        if (!SetSpeedEditorConversion.SynchronizeDraft(level, target, currentBpm, switchToMultiplier))
            throw new InvalidOperationException("Bpm -> Multiplier mode switch was not synchronized.");
        AssertNear(100.0, ReadDouble(switchToMultiplier["beatsPerMinute"]), "mode switch preserves effective BPM");
        AssertNear(0.5, ReadDouble(switchToMultiplier["bpmMultiplier"]), "mode switch derives multiplier");
        AssertEqual("preserve-me", switchToMultiplier["unknownPayload"]?.GetValue<string>(), "unknown property preservation");

        LevelAction multiplierTarget = target with
        {
            SpeedType = "Multiplier",
            BeatsPerMinute = 100.0,
            BpmMultiplier = 0.5
        };
        var currentMultiplier = (JsonObject)switchToMultiplier.DeepClone();
        JsonObject switchBackToBpm = (JsonObject)currentMultiplier.DeepClone();
        switchBackToBpm["speedType"] = "Bpm";
        if (!SetSpeedEditorConversion.SynchronizeDraft(level, multiplierTarget, currentMultiplier, switchBackToBpm))
            throw new InvalidOperationException("Multiplier -> Bpm mode switch was not synchronized.");
        AssertNear(100.0, ReadDouble(switchBackToBpm["beatsPerMinute"]), "round-trip BPM");
        AssertNear(0.5, ReadDouble(switchBackToBpm["bpmMultiplier"]), "round-trip multiplier");

        JsonObject editBpm = (JsonObject)currentBpm.DeepClone();
        editBpm["beatsPerMinute"] = 150.0;
        if (!SetSpeedEditorConversion.SynchronizeDraft(level, target, currentBpm, editBpm))
            throw new InvalidOperationException("BPM field edit was not synchronized.");
        AssertNear(0.75, ReadDouble(editBpm["bpmMultiplier"]), "BPM edit updates multiplier");

        JsonObject editMultiplier = (JsonObject)currentMultiplier.DeepClone();
        editMultiplier["bpmMultiplier"] = 1.25;
        if (!SetSpeedEditorConversion.SynchronizeDraft(level, multiplierTarget, currentMultiplier, editMultiplier))
            throw new InvalidOperationException("Multiplier field edit was not synchronized.");
        AssertNear(250.0, ReadDouble(editMultiplier["beatsPerMinute"]), "multiplier edit updates BPM");

        if (SetSpeedEditorConversion.TryConvertBpmToMultiplier(0.0, 100.0, out _) ||
            SetSpeedEditorConversion.TryConvertBpmToMultiplier(200.0, double.NaN, out _) ||
            SetSpeedEditorConversion.TryConvertMultiplierToBpm(200.0, double.PositiveInfinity, out _))
        {
            throw new InvalidOperationException("SetSpeed conversion accepted an invalid numeric value.");
        }

        JsonObject invalid = (JsonObject)currentBpm.DeepClone();
        invalid["beatsPerMinute"] = -1.0;
        SetSpeedEditorConversion.SynchronizeDraft(level, target, currentBpm, invalid);
        AssertNear(100.0, ReadDouble(invalid["beatsPerMinute"]), "invalid BPM edit is rejected");
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue json)
        {
            if (json.TryGetValue(out double value))
                return value;
            if (json.TryGetValue(out int integer))
                return integer;
        }
        throw new InvalidOperationException("Expected numeric JSON value.");
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }

    private static void AssertEqual(string expected, string? actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual ?? "<null>"}.");
    }
}
