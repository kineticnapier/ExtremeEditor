using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TrackPositionEditorOnlyRegression
{
    internal static void Run()
    {
        VerifySetSpeedEditorConversionContract();

        LevelDocument level = LevelDocument.CreateSynthetic(4);
        var action = new LevelAction(1, "PositionTrack", true, null, null, null, null)
        {
            SourceIndex = -2,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "PositionTrack",
                ["positionOffset"] = new JsonArray(2.0, 0.0),
                ["relativeTo"] = new JsonArray(0, "ThisTile"),
                ["editorOnly"] = true,
                ["unknownEditorPayload"] = "preserve-me"
            }
        };
        level.ReplaceActions([action]);

        StaticTrackTransform[] editor = TrackTransformResolver.ResolveStatic(level);
        float expectedX = level.Positions[1].X + 2f * PathBuilder.DefaultLongTileSize;
        AssertNear(expectedX, editor[1].X, "editor preview PositionTrack.editorOnly");

        Type resolver = typeof(TrackTransformResolver);
        Type? contextType = resolver.Assembly.GetType("ExtremeEditor.Wpf.Native.TrackTransformResolveContext");
        MethodInfo? contextual = contextType is null
            ? null
            : resolver.GetMethod(
                "ResolveStatic",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(LevelDocument), contextType],
                modifiers: null);
        if (contextType is null || contextual is null)
        {
            throw new InvalidOperationException(
                "PositionTrack.editorOnly has no explicit editor/runtime resolution boundary.");
        }

        object runtimeContext = Enum.Parse(contextType, "Runtime");
        var runtime = (StaticTrackTransform[])(contextual.Invoke(null, [level, runtimeContext])
            ?? throw new InvalidOperationException("Runtime PositionTrack resolution returned null."));
        AssertNear(level.Positions[1].X, runtime[1].X, "runtime excludes editor-only PositionTrack");

        var editorSession = new EditorSession(level);
        JsonObject editable = AdoFaiEditorSaveService.BuildEditableActionJson(editorSession, action);
        if (editable["editorOnly"]?.GetValue<bool>() != true ||
            editable["unknownEditorPayload"]?.GetValue<string>() != "preserve-me")
        {
            throw new InvalidOperationException("PositionTrack editor-only/unknown properties were not preserved for save.");
        }

        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor.TrackPosition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "editor-only.adofai");
        try
        {
            editorSession.SaveAsync(path).GetAwaiter().GetResult();
            JsonObject saved = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("Saved PositionTrack fixture was not a JSON object.");
            JsonObject savedAction = saved["actions"]?.AsArray()
                .OfType<JsonObject>()
                .Single(item => item["eventType"]?.GetValue<string>() == "PositionTrack")
                ?? throw new InvalidOperationException("Saved PositionTrack action is missing.");
            if (savedAction["editorOnly"]?.GetValue<bool>() != true ||
                savedAction["unknownEditorPayload"]?.GetValue<string>() != "preserve-me")
            {
                throw new InvalidOperationException("PositionTrack save round-trip lost editor-only/unknown properties.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySetSpeedEditorConversionContract()
    {
        AssertNear(0.5, BpmToMultiplier(200.0, 100.0), "SetSpeed 200 BPM -> 100 BPM");
        AssertNear(100.0, MultiplierToBpm(200.0, 0.5), "SetSpeed 200 BPM x 0.5");

        double previousEffectiveBpm = 100.0 * 2.0;
        AssertNear(200.0, previousEffectiveBpm, "SetSpeed previous effective BPM");
        AssertNear(100.0, MultiplierToBpm(previousEffectiveBpm, 0.5), "SetSpeed chained multiplier");

        double roundTrip = MultiplierToBpm(200.0, BpmToMultiplier(200.0, 137.5));
        AssertNear(137.5, roundTrip, "SetSpeed Bpm -> Multiplier -> Bpm round-trip");

        Type? helper = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.SetSpeedEditorConversion");
        if (helper is null)
        {
            throw new InvalidOperationException(
                "SetSpeed BPM/multiplier editor conversion helper is not available.");
        }

        RequireSetSpeedMethod(helper, "TryGetPreviousEffectiveBpm");
        RequireSetSpeedMethod(helper, "TryConvertBpmToMultiplier");
        RequireSetSpeedMethod(helper, "TryConvertMultiplierToBpm");
    }

    private static void RequireSetSpeedMethod(Type helper, string name)
    {
        MethodInfo? method = helper.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null)
            throw new InvalidOperationException($"SetSpeed editor conversion helper is missing {name}.");
    }

    private static double BpmToMultiplier(double previousBpm, double targetBpm)
    {
        if (!IsValidSpeedValue(previousBpm) || !IsValidSpeedValue(targetBpm))
            throw new InvalidOperationException("SetSpeed reference BPM conversion received an invalid value.");
        return targetBpm / previousBpm;
    }

    private static double MultiplierToBpm(double previousBpm, double multiplier)
    {
        if (!IsValidSpeedValue(previousBpm) || !IsValidSpeedValue(multiplier))
            throw new InvalidOperationException("SetSpeed reference multiplier conversion received an invalid value.");
        return previousBpm * multiplier;
    }

    private static bool IsValidSpeedValue(double value) =>
        double.IsFinite(value) && value > 0.0;

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }

    private static void AssertNear(float expected, float actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.0001f)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }
}
