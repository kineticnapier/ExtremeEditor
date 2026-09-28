using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsPlanetPlacementRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingPlanetTransform =
        "MoveDecorations planet placement does not match game follow semantics.";

    [ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_PLANET_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations planet placement regression is valid.");
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
        PlanetWorldPositionContract contract = PlanetWorldPositionContract.Discover();
        InputSnapshot before = InputSnapshot.Create(level, timeline);

        VerifyPlanet(level, timeline, contract, "RedPlanet", 0, new Vector2(10, 20), (17.5, 24.5));
        VerifyPlanet(level, timeline, contract, "BluePlanet", 1, new Vector2(-4, 8), (3.5, 12.5));
        VerifyPlanet(level, timeline, contract, "GreenPlanet", 2, new Vector2(0, -6), (7.5, -1.5));

        LevelDecoration red = FindDecoration(level, 0);
        VfxOccurrence redMove = FindOccurrence(timeline, 0);
        DecorationState redState = DecorationState.Evaluate(red, timeline, redMove.StartTime);
        AssertPair(redState.Position, (5, 3),
            "Planet placement leaked renderer follow position into DecorationState logical coordinates.");

        Vector2 first = contract.Resolve(level, red, redState, _ => new Vector2(10, 20));
        Vector2 second = contract.Resolve(level, red, redState, _ => new Vector2(20, 30));
        AssertPair((second.X - first.X, second.Y - first.Y), (10, 10),
            "Planet placement did not follow the current planet transform dynamically.");
        AssertPair(redState.Position, (5, 3),
            "Dynamic planet follow mutated DecorationState logical coordinates.");

        if (!before.Matches(InputSnapshot.Create(level, timeline)))
            throw new InvalidOperationException("MoveDecorations planet placement mutated its input model.");
    }

    private static void VerifyPlanet(
        LevelDocument level,
        VfxTimeline timeline,
        PlanetWorldPositionContract contract,
        string placement,
        int sourceIndex,
        Vector2 planetWorld,
        (double X, double Y) expectedWorld)
    {
        LevelDecoration decoration = FindDecoration(level, sourceIndex);
        VfxOccurrence move = FindOccurrence(timeline, sourceIndex);
        DecorationState state = DecorationState.Evaluate(decoration, timeline, move.StartTime);

        AssertPair(state.Position, (5, 3),
            $"{placement} MoveDecorations did not preserve chart-space logical position.");

        Vector2 resolved = contract.Resolve(
            level,
            decoration,
            state,
            requestedPlacement => string.Equals(requestedPlacement, placement, StringComparison.Ordinal)
                ? planetWorld
                : throw new InvalidOperationException(
                    $"Planet resolver requested unexpected placement '{requestedPlacement}'."));

        AssertPair((resolved.X, resolved.Y), expectedWorld, MissingPlanetTransform);
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations(
        [
            CreateDecoration(0, "red", "RedPlanet"),
            CreateDecoration(1, "blue", "BluePlanet"),
            CreateDecoration(2, "green", "GreenPlanet")
        ]);
        level.ReplaceActions(
        [
            CreateMove(1, 0, "red", "RedPlanet"),
            CreateMove(1, 1, "blue", "BluePlanet"),
            CreateMove(1, 2, "green", "GreenPlanet")
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
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100
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

    private sealed class PlanetWorldPositionContract
    {
        private readonly MethodInfo _method;
        private readonly Type _resolverType;

        private PlanetWorldPositionContract(MethodInfo method, Type resolverType)
        {
            _method = method;
            _resolverType = resolverType;
        }

        public static PlanetWorldPositionContract Discover()
        {
            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (method.Name is not ("ResolveWorldPosition" or "ToWorldPosition"))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 4 ||
                        parameters[0].ParameterType != typeof(LevelDocument) ||
                        parameters[1].ParameterType != typeof(LevelDecoration) ||
                        parameters[2].ParameterType != typeof(DecorationState))
                    {
                        continue;
                    }

                    Type resolverType = parameters[3].ParameterType;
                    if (!typeof(Delegate).IsAssignableFrom(resolverType))
                        continue;

                    return new PlanetWorldPositionContract(method, resolverType);
                }
            }

            throw new InvalidOperationException(MissingPlanetTransform);
        }

        public Vector2 Resolve(
            LevelDocument level,
            LevelDecoration decoration,
            DecorationState state,
            Func<string, Vector2> resolver)
        {
            Delegate compatibleResolver = Delegate.CreateDelegate(
                _resolverType,
                resolver.Target,
                resolver.Method);
            object? result = _method.Invoke(null, [level, decoration, state, compatibleResolver]);
            return result is Vector2 vector
                ? vector
                : throw new InvalidOperationException("Planet world-position resolver returned no Vector2.");
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
