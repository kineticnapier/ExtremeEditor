using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsPivotOffsetRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingPivot =
        "MoveDecorations pivotOffset is not represented in DecorationState/screen placement.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_PIVOT_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations pivotOffset regression is valid.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations([CreateDecoration()]);
        level.ReplaceActions([CreateMove()]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        LevelDecoration decoration = level.Decorations.Single();
        VfxOccurrence move = timeline.Occurrences.Single(item =>
            item.EventType == "MoveDecorations" && !item.IsRepeated);
        DecorationState state = DecorationState.Evaluate(decoration, timeline, move.StartTime);

        (double pivotX, double pivotY) = ReadPivot(state);
        AssertNear(pivotX, 3, MissingPivot);
        AssertNear(pivotY, 4, MissingPivot);

        // Camera placement uses pivotPosVec + pivotOffsetVec before /20 + (0.5, 0.5).
        // Therefore a MoveDecorations pivotOffset must affect the resolved screen position,
        // rather than leaving the AddDecoration pivotOffset frozen forever.
        Vector2 screen = DecorationWorldTransformResolver.ResolveScreenRelativePosition(
            decoration,
            state,
            screenWidth: 1920,
            screenHeight: 1080);
        AssertPair(screen, new Vector2(0.75f, 0.7f), MissingPivot);
    }

    private static LevelDecoration CreateDecoration() =>
        new(0, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["floor"] = 0,
                ["eventType"] = "AddDecoration",
                ["tag"] = "pivot",
                ["relativeTo"] = "Camera",
                ["position"] = new JsonArray(2, 0),
                ["pivotOffset"] = new JsonArray(1, -1),
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100
            }
        };

    private static LevelAction CreateMove()
    {
        var properties = new JsonObject
        {
            ["floor"] = 0,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = 0,
            ["duration"] = 0,
            ["ease"] = "Linear",
            ["tag"] = "pivot",
            ["pivotOffset"] = new JsonArray(3, 4)
        };
        return new LevelAction(0, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = 0,
            AngleOffset = 0,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static (double X, double Y) ReadPivot(DecorationState state)
    {
        Type type = state.GetType();
        PropertyInfo? pair = type.GetProperty("PivotOffset", BindingFlags.Public | BindingFlags.Instance);
        if (pair?.GetValue(state) is ValueTuple<double, double> tuple)
            return tuple;

        PropertyInfo? xProperty = type.GetProperty("PivotOffsetX", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? yProperty = type.GetProperty("PivotOffsetY", BindingFlags.Public | BindingFlags.Instance);
        if (xProperty?.GetValue(state) is double x && yProperty?.GetValue(state) is double y)
            return (x, y);

        throw new InvalidOperationException(MissingPivot);
    }

    private static void AssertNear(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > Tolerance)
            throw new InvalidOperationException(message);
    }

    private static void AssertPair(Vector2 actual, Vector2 expected, string message)
    {
        if (Math.Abs(actual.X - expected.X) > Tolerance ||
            Math.Abs(actual.Y - expected.Y) > Tolerance)
        {
            throw new InvalidOperationException(message);
        }
    }
}
