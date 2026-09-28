using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsParallaxRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingParallaxTransform =
        "MoveDecorations parallax placement does not match game camera semantics.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_PARALLAX_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations parallax regression is valid.");
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
        LevelDocument level = CreateFixture();
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        ParallaxWorldPositionContract contract = ParallaxWorldPositionContract.Discover();

        Vector2 cameraAtStart = new(10, 20);
        Vector2 cameraNow = new(14, 12);

        LevelDecoration parallax = FindDecoration(level, 0);
        VfxOccurrence parallaxMove = FindOccurrence(timeline, 0);
        DecorationState parallaxState = DecorationState.Evaluate(parallax, timeline, parallaxMove.StartTime);
        AssertPair(parallaxState.Position, (5, 3),
            "Parallax world placement leaked camera state into logical DecorationState position.");

        // Stock editor inversion exposes the runtime composition as:
        //   base + (cameraNow - cameraAtStart) * parallaxMultiplier + parallaxOffset * tileSize
        // with multiplier = parallax / 100. The fixture uses asymmetric values so
        // missing/divided/twice-applied terms are all detectable.
        Vector2 resolved = contract.Resolve(level, parallax, parallaxState, cameraNow, cameraAtStart);
        AssertPair((resolved.X, resolved.Y), (14, -0.5), MissingParallaxTransform);

        LevelDecoration zero = FindDecoration(level, 1);
        VfxOccurrence zeroMove = FindOccurrence(timeline, 1);
        DecorationState zeroState = DecorationState.Evaluate(zero, timeline, zeroMove.StartTime);
        Vector2 zeroResolved = contract.Resolve(level, zero, zeroState, cameraNow, cameraAtStart);
        AssertPair((zeroResolved.X, zeroResolved.Y), (7.5, 4.5),
            "Zero parallax should ignore camera delta and parallaxOffset in stock semantics.");

        Vector2 movedAgain = contract.Resolve(level, parallax, parallaxState, new Vector2(18, 16), cameraAtStart);
        AssertPair(
            (movedAgain.X - resolved.X, movedAgain.Y - resolved.Y),
            (2, 1),
            "Parallax world placement did not track camera movement using parallax/100 multiplier.");

        AssertPair(parallaxState.Position, (5, 3),
            "Parallax resolver mutated DecorationState logical coordinates.");
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations(
        [
            CreateDecoration(0, "parallax", new JsonArray(50, 25), new JsonArray(3, -2)),
            CreateDecoration(1, "zero", new JsonArray(0, 0), new JsonArray(99, 99))
        ]);
        level.ReplaceActions(
        [
            CreateMove(1, 0, "parallax"),
            CreateMove(1, 1, "zero")
        ]);
        return level;
    }

    private static LevelDecoration CreateDecoration(
        int sourceIndex,
        string tag,
        JsonArray parallax,
        JsonArray parallaxOffset) =>
        new(1, "AddDecoration")
        {
            SourceIndex = sourceIndex,
            Properties = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["tag"] = tag,
                ["relativeTo"] = "Global",
                ["position"] = new JsonArray(2, -1),
                ["parallax"] = parallax,
                ["parallaxOffset"] = parallaxOffset,
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100
            }
        };

    private static LevelAction CreateMove(int floor, int sourceIndex, string tag)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = 0,
            ["duration"] = 0,
            ["ease"] = "Linear",
            ["tag"] = tag,
            ["relativeTo"] = "Global",
            ["positionOffset"] = new JsonArray(3, 4)
        };
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = 0,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static LevelDecoration FindDecoration(LevelDocument level, int sourceIndex) =>
        level.Decorations.Single(item => item.SourceIndex == sourceIndex);

    private static VfxOccurrence FindOccurrence(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(item => item.SourceIndex == sourceIndex && !item.IsRepeated);

    private static void AssertPair(
        (double X, double Y) actual,
        (double X, double Y) expected,
        string message)
    {
        if (Math.Abs(actual.X - expected.X) > Tolerance ||
            Math.Abs(actual.Y - expected.Y) > Tolerance)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ParallaxWorldPositionContract
    {
        private readonly MethodInfo _method;

        private ParallaxWorldPositionContract(MethodInfo method)
        {
            _method = method;
        }

        public static ParallaxWorldPositionContract Discover()
        {
            Type[] parameterTypes =
            [
                typeof(LevelDocument),
                typeof(LevelDecoration),
                typeof(DecorationState),
                typeof(Vector2),
                typeof(Vector2)
            ];

            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase))
                    continue;

                MethodInfo? method = type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(candidate =>
                        candidate.Name is "ResolveWorldPosition" or "ToWorldPosition" &&
                        candidate.GetParameters().Select(parameter => parameter.ParameterType)
                            .SequenceEqual(parameterTypes));
                if (method is not null)
                    return new ParallaxWorldPositionContract(method);
            }

            throw new InvalidOperationException(MissingParallaxTransform);
        }

        public Vector2 Resolve(
            LevelDocument level,
            LevelDecoration decoration,
            DecorationState state,
            Vector2 cameraNow,
            Vector2 cameraAtStart)
        {
            object? result = _method.Invoke(null, [level, decoration, state, cameraNow, cameraAtStart]);
            return result is Vector2 vector
                ? vector
                : throw new InvalidOperationException("Parallax world-position resolver returned no Vector2.");
        }
    }
}
