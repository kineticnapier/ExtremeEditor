using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsCompletionRegression
{
    private const double Tolerance = 1e-6;
    private const string Failure =
        "MoveDecorations completion contract is not fully represented in DecorationState/render placement.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_COMPLETION_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations completion regression is valid.");
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

        // Null axes mean "leave that component untouched", not "ignore the entire pair".
        AssertNear(state.PositionX, 10, "positionOffset null X changed the X component.");
        AssertNear(state.PositionY, 50, "positionOffset null-axis inheritance failed for Y.");
        AssertNear(state.ScaleX, 300, "scale X target was not applied.");
        AssertNear(state.ScaleY, 200, "scale null Y did not preserve the prior Y component.");
        AssertNear(state.Opacity, 40, "opacity target was not applied with the combined MoveDecorations event.");

        (double pivotX, double pivotY) = ReadPair(state, "PivotOffset", "PivotOffsetX", "PivotOffsetY");
        AssertNear(pivotX, 3, "pivotOffset X target was not applied.");
        AssertNear(pivotY, 4, "pivotOffset Y target was not applied.");

        (double parallaxX, double parallaxY) = ReadPair(
            state,
            "ParallaxOffset",
            "ParallaxOffsetX",
            "ParallaxOffsetY");
        AssertNear(parallaxX, 1, "parallaxOffset null X did not preserve the prior X component.");
        AssertNear(parallaxY, 6, "parallaxOffset Y target was not applied.");

        // Camera placement must use the evaluated MoveDecorations pivot, not the AddDecoration pivot.
        Vector2 screen = DecorationWorldTransformResolver.ResolveScreenRelativePosition(
            decoration,
            state,
            screenWidth: 1920,
            screenHeight: 1080);
        AssertPair(screen, new Vector2(1.15f, 3.2f),
            "camera placement did not compose the evaluated position and pivotOffset.");

        // Renderer-facing parallax composition must likewise consume the evaluated parallaxOffset.
        Vector2 world = ResolveParallaxWorldPosition(level, decoration, state);
        Vector2 expectedBase = new(15f, 75f); // evaluated position * tileSize (1.5)
        Vector2 expectedParallaxOffset = new(1.5f, 9f);
        Vector2 expectedCameraDelta = new(4f, -2f); // (12,3) - (8,5), multiplier 100% / 50%
        Vector2 expected = expectedBase + new Vector2(4f, -1f) + expectedParallaxOffset;
        AssertPair(world, expected,
            "parallax placement did not compose evaluated MoveDecorations parallaxOffset.");
    }

    private static LevelDecoration CreateDecoration() =>
        new(0, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["floor"] = 0,
                ["eventType"] = "AddDecoration",
                ["tag"] = "completion",
                ["relativeTo"] = "Camera",
                ["position"] = new JsonArray(10, 20),
                ["pivotOffset"] = new JsonArray(1, -1),
                ["rotation"] = 15,
                ["scale"] = new JsonArray(100, 200),
                ["opacity"] = 80,
                ["parallax"] = new JsonArray(100, 50),
                ["parallaxOffset"] = new JsonArray(1, 2)
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
            ["tag"] = "completion",
            ["positionOffset"] = new JsonArray(null, 30),
            ["pivotOffset"] = new JsonArray(3, 4),
            ["scale"] = new JsonArray(300, null),
            ["opacity"] = 40,
            ["parallaxOffset"] = new JsonArray(null, 6)
        };

        return new LevelAction(0, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = 0,
            AngleOffset = 0,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static (double X, double Y) ReadPair(
        DecorationState state,
        string pairPropertyName,
        string xPropertyName,
        string yPropertyName)
    {
        Type type = state.GetType();
        PropertyInfo? pair = type.GetProperty(pairPropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (pair?.GetValue(state) is ValueTuple<double, double> tuple)
            return tuple;

        PropertyInfo? xProperty = type.GetProperty(xPropertyName, BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? yProperty = type.GetProperty(yPropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (TryReadNumber(xProperty?.GetValue(state), out double x) &&
            TryReadNumber(yProperty?.GetValue(state), out double y))
        {
            return (x, y);
        }

        throw new InvalidOperationException(Failure);
    }

    private static Vector2 ResolveParallaxWorldPosition(
        LevelDocument level,
        LevelDecoration decoration,
        DecorationState state)
    {
        MethodInfo? method = typeof(DecorationWorldTransformResolver).GetMethod(
            "ResolveWorldPosition",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(LevelDocument), typeof(LevelDecoration), typeof(DecorationState), typeof(Vector2), typeof(Vector2)],
            modifiers: null);

        if (method is null)
            throw new InvalidOperationException(Failure);

        try
        {
            object? result = method.Invoke(null, [level, decoration, state, new Vector2(12, 3), new Vector2(8, 5)]);
            return result is Vector2 value ? value : throw new InvalidOperationException(Failure);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException)
        {
            // Camera placement has separate screen-clamp semantics. The completion contract still
            // requires a renderer-facing parallax composition path that can consume state offsets.
            MethodInfo? alternate = typeof(DecorationWorldTransformResolver).GetMethod(
                "ResolveParallaxWorldPosition",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(LevelDocument), typeof(LevelDecoration), typeof(DecorationState), typeof(Vector2), typeof(Vector2)],
                modifiers: null);
            if (alternate?.Invoke(null, [level, decoration, state, new Vector2(12, 3), new Vector2(8, 5)]) is Vector2 value)
                return value;
            throw new InvalidOperationException(Failure);
        }
    }

    private static bool TryReadNumber(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            default:
                result = 0;
                return false;
        }
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
