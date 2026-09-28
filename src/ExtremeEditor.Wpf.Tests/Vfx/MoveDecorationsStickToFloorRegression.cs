using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsStickToFloorRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingStickToFloor =
        "MoveDecorations stickToFloor position does not match game floor-follow semantics.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_STICK_TO_FLOOR_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations stickToFloor regression is valid.");
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
        LevelDecoration decoration = level.Decorations.Single();
        VfxOccurrence move = timeline.Occurrences.Single(item =>
            item.EventType == "MoveDecorations" && !item.IsRepeated);
        DecorationState state = DecorationState.Evaluate(decoration, timeline, move.StartTime);

        AssertPair(state.Position, (5, 0),
            "stickToFloor leaked runtime floor movement into logical DecorationState position.");

        Vector2 baseWorld = DecorationWorldTransformResolver.ResolveWorldPosition(level, decoration, state);
        AssertPair(baseWorld, new Vector2(17.5f, 20f),
            "Base Tile placement changed before stickToFloor follow was applied.");

        Vector2 initialStartPosition = new(13f, 14f);
        Vector2 currentFloorWorld = new(14f, 23f);
        Vector2 resolved = StickToFloorContract.Discover().Resolve(
            decoration,
            baseWorld,
            currentFloorWorld,
            initialStartPosition);

        // scrDecoration.UpdatePosition:
        // vector2 = pivotPosVec;
        // if (stickToFloor)
        //     vector2 += parentFloor.transform.position - startPos;
        AssertPair(resolved, new Vector2(18.5f, 29f), MissingStickToFloor);

        // The follow transform is dynamic: changing the current floor position changes only
        // final world placement, not the MoveDecorations logical state or base resolver result.
        Vector2 movedFloor = new(20f, 10f);
        Vector2 resolvedAgain = StickToFloorContract.Discover().Resolve(
            decoration,
            baseWorld,
            movedFloor,
            initialStartPosition);
        AssertPair(resolvedAgain, new Vector2(24.5f, 16f),
            "stickToFloor did not follow the current parent floor position dynamically.");
        AssertPair(state.Position, (5, 0),
            "stickToFloor mutated DecorationState logical coordinates.");
        AssertPair(baseWorld, new Vector2(17.5f, 20f),
            "stickToFloor mutated the pre-follow world placement.");

        LevelDecoration disabled = CreateDecoration(stickToFloor: false);
        Vector2 unchanged = StickToFloorContract.Discover().Resolve(
            disabled,
            baseWorld,
            currentFloorWorld,
            initialStartPosition);
        AssertPair(unchanged, baseWorld,
            "stickToFloor=false unexpectedly applied a floor-follow delta.");
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.Positions[1] = new Vector2(10f, 20f);
        level.ReplaceDecorations([CreateDecoration(stickToFloor: true)]);
        level.ReplaceActions([CreateMove()]);
        return level;
    }

    private static LevelDecoration CreateDecoration(bool stickToFloor) =>
        new(1, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["tag"] = "follow",
                ["relativeTo"] = "Tile",
                ["stickToFloor"] = stickToFloor,
                ["position"] = new JsonArray(2, -4),
                ["pivotOffset"] = new JsonArray(0, 0),
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100
            }
        };

    private static LevelAction CreateMove()
    {
        var properties = new JsonObject
        {
            ["floor"] = 1,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = 0,
            ["duration"] = 0,
            ["ease"] = "Linear",
            ["tag"] = "follow",
            ["positionOffset"] = new JsonArray(3, 4)
        };
        return new LevelAction(1, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = 0,
            AngleOffset = 0,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static void AssertPair((double X, double Y) actual, (double X, double Y) expected, string message)
    {
        if (Math.Abs(actual.X - expected.X) > Tolerance ||
            Math.Abs(actual.Y - expected.Y) > Tolerance)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertPair(Vector2 actual, Vector2 expected, string message)
    {
        if (Math.Abs(actual.X - expected.X) > Tolerance ||
            Math.Abs(actual.Y - expected.Y) > Tolerance)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StickToFloorContract
    {
        private readonly MethodInfo _method;

        private StickToFloorContract(MethodInfo method)
        {
            _method = method;
        }

        public static StickToFloorContract Discover()
        {
            Type[] parameterTypes =
            [
                typeof(LevelDecoration),
                typeof(Vector2),
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
                        candidate.Name is "ResolveStickToFloorWorldPosition" or "ApplyStickToFloorPosition" &&
                        candidate.ReturnType == typeof(Vector2) &&
                        candidate.GetParameters().Select(parameter => parameter.ParameterType)
                            .SequenceEqual(parameterTypes));
                if (method is not null)
                    return new StickToFloorContract(method);
            }

            throw new InvalidOperationException(MissingStickToFloor);
        }

        public Vector2 Resolve(
            LevelDecoration decoration,
            Vector2 baseWorld,
            Vector2 parentFloorWorld,
            Vector2 startPosition)
        {
            object? result = _method.Invoke(null, [decoration, baseWorld, parentFloorWorld, startPosition]);
            return result is Vector2 vector
                ? vector
                : throw new InvalidOperationException("stickToFloor resolver returned no Vector2.");
        }
    }
}
