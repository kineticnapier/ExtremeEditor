using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsTilePlacementRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingWorldTransform =
        "MoveDecorations Tile placement does not match game world transform semantics.";

    public static void Run()
    {
        LevelDocument level = CreateFixture();
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        WorldPositionContract contract = WorldPositionContract.Discover();
        InputSnapshot before = InputSnapshot.Create(level, timeline);

        LevelDecoration tile = FindDecoration(level, 0);
        LevelDecoration global = FindDecoration(level, 1);
        VfxOccurrence tileMove = FindOccurrence(timeline, 0);
        VfxOccurrence globalMove = FindOccurrence(timeline, 1);
        Vector2 floorWorld = level.Positions[1];
        double tileSize = PathBuilder.DefaultLongTileSize;

        DecorationState tileInitial = DecorationState.Evaluate(
            tile, timeline, tileMove.StartTime - 0.001);
        AssertPair(tileInitial.Position, (2, -1),
            "Tile placement changed the chart-space AddDecoration position.");
        AssertPair(
            contract.Resolve(level, tile, tileInitial),
            (floorWorld.X + 2 * tileSize, floorWorld.Y - tileSize),
            "Tile AddDecoration initial placement did not apply floor world position and tileSize once.");

        DecorationState tileMoved = DecorationState.Evaluate(tile, timeline, tileMove.StartTime);
        AssertPair(tileMoved.Position, (5, 3),
            "Tile MoveDecorations did not keep its chart-space logical position.");
        AssertPair(
            contract.Resolve(level, tile, tileMoved),
            (floorWorld.X + 5 * tileSize, floorWorld.Y + 3 * tileSize),
            MissingWorldTransform);

        DecorationState globalMoved = DecorationState.Evaluate(global, timeline, globalMove.StartTime);
        AssertPair(globalMoved.Position, (5, 3),
            "Global MoveDecorations did not preserve its chart-space logical position.");
        AssertPair(
            contract.Resolve(level, global, globalMoved),
            (5 * tileSize, 3 * tileSize),
            "Global decoration world placement unexpectedly included a floor contribution.");

        (double X, double Y) tileWorld = contract.Resolve(level, tile, tileMoved);
        (double X, double Y) globalWorld = contract.Resolve(level, global, globalMoved);
        AssertPair(
            (tileWorld.X - globalWorld.X, tileWorld.Y - globalWorld.Y),
            (floorWorld.X, floorWorld.Y),
            "Tile and Global placement did not differ by exactly the source floor world position.");

        if (!before.Matches(InputSnapshot.Create(level, timeline)))
            throw new InvalidOperationException("MoveDecorations Tile placement mutated its input model.");
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations(
        [
            CreateDecoration(0, "tile", "Tile"),
            CreateDecoration(1, "global", "Global")
        ]);
        level.ReplaceActions(
        [
            CreateMove(1, 0, "tile", "Tile"),
            CreateMove(1, 1, "global", "Global")
        ]);
        return level;
    }

    private static LevelDecoration CreateDecoration(int sourceIndex, string tag, string relativeTo) =>
        new(1, "AddDecoration")
        {
            SourceIndex = sourceIndex,
            Properties = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["tag"] = tag,
                ["relativeTo"] = relativeTo,
                ["position"] = new JsonArray(2, -1),
                ["rotation"] = 37,
                ["scale"] = new JsonArray(80, 125),
                ["opacity"] = 70
            }
        };

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        string relativeTo)
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
            ["relativeTo"] = relativeTo,
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

    private sealed class WorldPositionContract
    {
        private readonly MethodInfo _method;

        private WorldPositionContract(MethodInfo method)
        {
            _method = method;
        }

        public static WorldPositionContract Discover()
        {
            Type[] parameterTypes =
                [typeof(LevelDocument), typeof(LevelDecoration), typeof(DecorationState)];
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
                    return new WorldPositionContract(method);
            }

            throw new InvalidOperationException(MissingWorldTransform);
        }

        public (double X, double Y) Resolve(
            LevelDocument level,
            LevelDecoration decoration,
            DecorationState state)
        {
            object? result = _method.Invoke(null, [level, decoration, state]);
            if (result is Vector2 vector)
                return (vector.X, vector.Y);
            if (result is ITuple tuple && tuple.Length >= 2)
                return (Convert.ToDouble(tuple[0]), Convert.ToDouble(tuple[1]));
            if (result is not null)
            {
                PropertyInfo? x = result.GetType().GetProperty("X");
                PropertyInfo? y = result.GetType().GetProperty("Y");
                if (x is not null && y is not null)
                    return (Convert.ToDouble(x.GetValue(result)), Convert.ToDouble(y.GetValue(result)));
            }
            throw new InvalidOperationException("Decoration world-position resolver returned no coordinate pair.");
        }
    }

    private sealed record InputSnapshot(string[] Decorations, string[] Actions, string[] Occurrences)
    {
        public static InputSnapshot Create(LevelDocument level, VfxTimeline timeline) => new(
            level.Decorations.Select(item => item.Properties.ToJsonString()).ToArray(),
            level.ActionStore.Actions.Select(item =>
                item.PropertyOverrides?.ToJsonString() ?? string.Empty).ToArray(),
            timeline.Occurrences.Select(item => string.Join(
                "|", item.Floor, item.SourceIndex, item.StartTime, item.Duration,
                item.RepeatIteration, item.SourceEvent.PropertyOverrides?.ToJsonString())).ToArray());

        public bool Matches(InputSnapshot other) =>
            Decorations.SequenceEqual(other.Decorations, StringComparer.Ordinal) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal) &&
            Occurrences.SequenceEqual(other.Occurrences, StringComparer.Ordinal);
    }
}
