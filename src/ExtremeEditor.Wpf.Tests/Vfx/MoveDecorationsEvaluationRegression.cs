using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsEvaluationRegression
{
    private const double Tolerance = 1e-9;
    private const string MissingEvaluatorMessage =
        "MoveDecorations evaluation core is not available.";

    public static void Run()
    {
        LevelDocument level = CreateFixture();
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        InputSnapshot before = InputSnapshot.Create(level, timeline);
        EvaluationContract contract = EvaluationContract.Discover();

        LevelDecoration hero = FindDecoration(level, 0);
        LevelDecoration unrelated = FindDecoration(level, 1);
        LevelDecoration repeated = FindDecoration(level, 2);

        VfxOccurrence moveA = FindOccurrence(timeline, 0, repeated: false);
        VfxOccurrence moveB = FindOccurrence(timeline, 1, repeated: false);
        VfxOccurrence sameTimeSecond = FindOccurrence(timeline, 4, repeated: false);

        object initial = contract.Evaluate(level, hero, timeline, moveA.StartTime - 0.001);
        AssertState(contract, initial, (1, 2), 10, (100, 200), 80,
            "MoveDecorations changed state before occurrence StartTime.");

        object afterA = contract.Evaluate(level, hero, timeline, moveA.StartTime);
        AssertState(contract, afterA, (10, 20), 30, (100, 200), 80,
            "duration=0 MoveDecorations was not applied immediately or reset unspecified properties.");

        double betweenAAndB = (moveA.StartTime + moveB.StartTime) * 0.5;
        object between = contract.Evaluate(level, hero, timeline, betweenAAndB);
        AssertState(contract, between, (10, 20), 30, (100, 200), 80,
            "MoveDecorations state was not retained until the next occurrence.");

        object afterB = contract.Evaluate(level, hero, timeline, moveB.StartTime);
        AssertState(contract, afterB, (10, 20), 30, (50, -100), 40,
            "A later MoveDecorations did not compose over the previously resolved state.");

        object afterTie = contract.Evaluate(level, hero, timeline, sameTimeSecond.StartTime);
        AssertState(contract, afterTie, (10, 20), 80, (50, -100), 40,
            "Same-time MoveDecorations did not follow VfxTimeline occurrence order.");

        object unaffected = contract.Evaluate(level, unrelated, timeline, sameTimeSecond.StartTime + 1);
        AssertState(contract, unaffected, (-1, -2), -15, (75, 125), 55,
            "A decoration with a non-matching tag was modified.");

        VerifyRepeatOccurrenceIsConsumed(contract, level, repeated, timeline);
        VerifyDeterministic(contract, level, hero, timeline, sameTimeSecond.StartTime);
        VerifyNonDestructive(level, timeline, before);
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.InitialBpm = 60;
        level.ReplaceDecorations(
        [
            CreateDecoration(0, "hero", 1, 2, 10, 100, 200, 80),
            CreateDecoration(1, "other", -1, -2, -15, 75, 125, 55),
            CreateDecoration(2, "repeat-target", 3, 4, 5, 100, 100, 100)
        ]);

        level.ReplaceActions(
        [
            CreateMove(0, 5, "hero", angleOffset: 90, active: false,
                position: (999, 999)),
            CreateMove(0, 0, "hero", angleOffset: 180,
                position: (10, 20), rotation: 30),
            CreateMove(0, 1, "hero", angleOffset: 360,
                scale: (50, -100), opacity: 40),
            CreateMove(0, 3, "hero", angleOffset: 540, rotation: 70),
            CreateMove(0, 4, "hero", angleOffset: 540, rotation: 80),

            CreateMove(1, 6, "repeat-target", angleOffset: 0,
                opacity: 25, eventTag: "repeat-move"),
            CreateRepeat(1, 7, "repeat-move"),
            CreateMove(1, 8, "repeat-target", angleOffset: 90, opacity: 80)
        ]);
        return level;
    }

    private static LevelDecoration CreateDecoration(
        int sourceIndex,
        string tag,
        double positionX,
        double positionY,
        double rotation,
        double scaleX,
        double scaleY,
        double opacity)
    {
        return new LevelDecoration(0, "AddDecoration")
        {
            SourceIndex = sourceIndex,
            Properties = new JsonObject
            {
                ["tag"] = tag,
                ["position"] = new JsonArray(positionX, positionY),
                ["rotation"] = rotation,
                ["scale"] = new JsonArray(scaleX, scaleY),
                ["opacity"] = opacity,
                ["futureData"] = new JsonObject { ["keep"] = true }
            }
        };
    }

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        bool active = true,
        (double X, double Y)? position = null,
        double? rotation = null,
        (double X, double Y)? scale = null,
        double? opacity = null,
        string? eventTag = null)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = active,
            ["angleOffset"] = angleOffset,
            ["duration"] = 0,
            ["ease"] = "Linear",
            ["tag"] = tag
        };
        if (position is { } positionValue)
            properties["position"] = new JsonArray(positionValue.X, positionValue.Y);
        if (rotation is double rotationValue)
            properties["rotation"] = rotationValue;
        if (scale is { } scaleValue)
            properties["scale"] = new JsonArray(scaleValue.X, scaleValue.Y);
        if (opacity is double opacityValue)
            properties["opacity"] = opacityValue;
        if (eventTag is not null)
            properties["eventTag"] = eventTag;

        return new LevelAction(floor, "MoveDecorations", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static LevelAction CreateRepeat(int floor, int sourceIndex, string tag)
    {
        return new LevelAction(floor, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "RepeatEvents",
                ["active"] = true,
                ["repeatType"] = "Beat",
                ["tag"] = tag,
                ["repetitions"] = 1,
                ["interval"] = 1,
                ["executeOnCurrentFloor"] = false
            }
        };
    }

    private static void VerifyRepeatOccurrenceIsConsumed(
        EvaluationContract contract,
        LevelDocument level,
        LevelDecoration decoration,
        VfxTimeline timeline)
    {
        VfxOccurrence original = FindOccurrence(timeline, 6, repeated: false);
        VfxOccurrence repeated = FindOccurrence(timeline, 6, repeated: true);
        VfxOccurrence intervening = FindOccurrence(timeline, 8, repeated: false);

        object afterOriginal = contract.Evaluate(level, decoration, timeline, original.StartTime);
        AssertState(contract, afterOriginal, (3, 4), 5, (100, 100), 25,
            "Initial repeated MoveDecorations occurrence was not applied.");

        object afterIntervening = contract.Evaluate(level, decoration, timeline, intervening.StartTime);
        AssertState(contract, afterIntervening, (3, 4), 5, (100, 100), 80,
            "Intervening MoveDecorations occurrence was not applied.");

        object afterRepeat = contract.Evaluate(level, decoration, timeline, repeated.StartTime);
        AssertState(contract, afterRepeat, (3, 4), 5, (100, 100), 25,
            "Evaluator did not consume the concrete RepeatEvents-derived occurrence.");
    }

    private static void VerifyDeterministic(
        EvaluationContract contract,
        LevelDocument level,
        LevelDecoration decoration,
        VfxTimeline timeline,
        double time)
    {
        object first = contract.Evaluate(level, decoration, timeline, time);
        object second = contract.Evaluate(level, decoration, timeline, time);
        if (!StateSignature(contract, first).Equals(StateSignature(contract, second)))
            throw new InvalidOperationException("MoveDecorations evaluation is not deterministic.");
    }

    private static void VerifyNonDestructive(
        LevelDocument level,
        VfxTimeline timeline,
        InputSnapshot before)
    {
        if (!before.Matches(InputSnapshot.Create(level, timeline)))
        {
            throw new InvalidOperationException(
                "MoveDecorations evaluation mutated document, source events, timeline, or decorations.");
        }
    }

    private static LevelDecoration FindDecoration(LevelDocument level, int sourceIndex) =>
        level.Decorations.Single(decoration => decoration.SourceIndex == sourceIndex);

    private static VfxOccurrence FindOccurrence(
        VfxTimeline timeline,
        int sourceIndex,
        bool repeated) =>
        timeline.Occurrences.Single(occurrence =>
            occurrence.SourceIndex == sourceIndex && occurrence.IsRepeated == repeated);

    private static void AssertState(
        EvaluationContract contract,
        object state,
        (double X, double Y) position,
        double rotation,
        (double X, double Y) scale,
        double opacity,
        string message)
    {
        (double X, double Y) actualPosition = contract.GetPosition(state);
        (double X, double Y) actualScale = contract.GetScale(state);
        if (!Near(position.X, actualPosition.X) || !Near(position.Y, actualPosition.Y) ||
            !Near(rotation, contract.GetRotation(state)) ||
            !Near(scale.X, actualScale.X) || !Near(scale.Y, actualScale.Y) ||
            !Near(opacity, contract.GetOpacity(state)))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static (double X, double Y, double Rotation, double ScaleX, double ScaleY, double Opacity)
        StateSignature(EvaluationContract contract, object state)
    {
        (double X, double Y) position = contract.GetPosition(state);
        (double X, double Y) scale = contract.GetScale(state);
        return (position.X, position.Y, contract.GetRotation(state), scale.X, scale.Y, contract.GetOpacity(state));
    }

    private static bool Near(double expected, double actual) =>
        Math.Abs(expected - actual) <= Tolerance;

    private sealed class EvaluationContract
    {
        private enum EvaluationShape
        {
            DecorationTimelineTime,
            DocumentDecorationTimelineTime,
            DocumentTimelineTime
        }

        private readonly MethodInfo _method;
        private readonly EvaluationShape _shape;

        private EvaluationContract(MethodInfo method, EvaluationShape shape)
        {
            _method = method;
            _shape = shape;
        }

        public static EvaluationContract Discover()
        {
            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase) ||
                    (!type.Name.Contains("Evaluator", StringComparison.OrdinalIgnoreCase) &&
                     !type.Name.Contains("State", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (method.Name is not ("Evaluate" or "EvaluateAt" or "Resolve"))
                        continue;

                    Type[] parameters = method.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .ToArray();
                    if (parameters.SequenceEqual(
                            [typeof(LevelDecoration), typeof(VfxTimeline), typeof(double)]))
                    {
                        return new EvaluationContract(method, EvaluationShape.DecorationTimelineTime);
                    }
                    if (parameters.SequenceEqual(
                            [typeof(LevelDocument), typeof(LevelDecoration), typeof(VfxTimeline), typeof(double)]))
                    {
                        return new EvaluationContract(method, EvaluationShape.DocumentDecorationTimelineTime);
                    }
                    if (parameters.SequenceEqual(
                            [typeof(LevelDocument), typeof(VfxTimeline), typeof(double)]))
                    {
                        return new EvaluationContract(method, EvaluationShape.DocumentTimelineTime);
                    }
                }
            }

            throw new InvalidOperationException(MissingEvaluatorMessage);
        }

        public object Evaluate(
            LevelDocument level,
            LevelDecoration decoration,
            VfxTimeline timeline,
            double timeSeconds)
        {
            object? result = _shape switch
            {
                EvaluationShape.DecorationTimelineTime =>
                    _method.Invoke(null, [decoration, timeline, timeSeconds]),
                EvaluationShape.DocumentDecorationTimelineTime =>
                    _method.Invoke(null, [level, decoration, timeline, timeSeconds]),
                EvaluationShape.DocumentTimelineTime =>
                    _method.Invoke(null, [level, timeline, timeSeconds]),
                _ => null
            };

            if (_shape != EvaluationShape.DocumentTimelineTime)
            {
                return result ?? throw new InvalidOperationException(
                    "MoveDecorations evaluator returned no decoration state.");
            }

            IEnumerable states = GetStateSequence(result);
            foreach (object? state in states)
            {
                if (state is not null && GetInt(state, "SourceIndex", "DecorationSourceIndex") == decoration.SourceIndex)
                    return state;
            }
            throw new InvalidOperationException(
                $"MoveDecorations evaluator omitted decoration {decoration.SourceIndex}.");
        }

        public (double X, double Y) GetPosition(object state) =>
            GetPair(state, "Position", "PositionX", "PositionY");

        public (double X, double Y) GetScale(object state) =>
            GetPair(state, "Scale", "ScaleX", "ScaleY");

        public double GetRotation(object state) =>
            GetDouble(state, "Rotation", "RotationDegrees");

        public double GetOpacity(object state) => GetDouble(state, "Opacity");

        private static IEnumerable GetStateSequence(object? result)
        {
            if (result is IEnumerable enumerable && result is not string)
                return enumerable;
            object? states = result is null ? null : GetValue(result, "States", "Decorations", "Items");
            return states as IEnumerable
                ?? throw new InvalidOperationException(
                    "MoveDecorations document evaluator did not return decoration states.");
        }

        private static (double X, double Y) GetPair(
            object state,
            string pairName,
            string xName,
            string yName)
        {
            object? pair = GetValue(state, pairName);
            if (pair is Vector2 vector)
                return (vector.X, vector.Y);
            if (pair is ITuple tuple && tuple.Length >= 2)
                return (Convert.ToDouble(tuple[0]), Convert.ToDouble(tuple[1]));
            if (pair is IEnumerable enumerable && pair is not string)
            {
                object?[] values = enumerable.Cast<object?>().Take(2).ToArray();
                if (values.Length == 2)
                    return (Convert.ToDouble(values[0]), Convert.ToDouble(values[1]));
            }
            if (pair is not null)
            {
                object? x = GetValue(pair, "X", "Item1");
                object? y = GetValue(pair, "Y", "Item2");
                if (x is not null && y is not null)
                    return (Convert.ToDouble(x), Convert.ToDouble(y));
            }
            return (GetDouble(state, xName), GetDouble(state, yName));
        }

        private static int GetInt(object source, params string[] names)
        {
            object? value = GetValue(source, names);
            return value is null
                ? throw new InvalidOperationException(
                    $"Decoration state does not expose {string.Join("/", names)}.")
                : Convert.ToInt32(value);
        }

        private static double GetDouble(object source, params string[] names)
        {
            object? value = GetValue(source, names);
            return value is null
                ? throw new InvalidOperationException(
                    $"Decoration state does not expose {string.Join("/", names)}.")
                : Convert.ToDouble(value);
        }

        private static object? GetValue(object source, params string[] names)
        {
            Type type = source.GetType();
            foreach (string name in names)
            {
                PropertyInfo? property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property is not null)
                    return property.GetValue(source);
                FieldInfo? field = type.GetField(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is not null)
                    return field.GetValue(source);
            }
            return null;
        }
    }

    private sealed class InputSnapshot
    {
        private InputSnapshot(
            string[] decorations,
            string[] actions,
            string[] occurrences,
            double[] angles,
            Vector2[] positions,
            WorldRect bounds)
        {
            Decorations = decorations;
            Actions = actions;
            Occurrences = occurrences;
            Angles = angles;
            Positions = positions;
            Bounds = bounds;
        }

        private string[] Decorations { get; }
        private string[] Actions { get; }
        private string[] Occurrences { get; }
        private double[] Angles { get; }
        private Vector2[] Positions { get; }
        private WorldRect Bounds { get; }

        public static InputSnapshot Create(LevelDocument level, VfxTimeline timeline) => new(
            level.Decorations.Select(decoration => string.Join(
                "|",
                decoration.Floor,
                decoration.SourceIndex,
                decoration.EventType,
                decoration.Properties.ToJsonString())).ToArray(),
            level.ActionStore.Actions.Select(action => string.Join(
                "|",
                action.Floor,
                action.SourceIndex,
                action.EventType,
                action.Active,
                action.AngleOffset,
                action.Duration,
                action.PropertyOverrides?.ToJsonString())).ToArray(),
            timeline.Occurrences.Select(occurrence => string.Join(
                "|",
                occurrence.Floor,
                occurrence.SourceIndex,
                occurrence.EventType,
                occurrence.Active,
                occurrence.AngleOffset,
                occurrence.Duration,
                occurrence.StartTime,
                occurrence.RepeatSourceIndex,
                occurrence.SourceEvent.PropertyOverrides?.ToJsonString())).ToArray(),
            level.Angles.ToArray(),
            level.Positions.ToArray(),
            level.Bounds);

        public bool Matches(InputSnapshot other) =>
            Decorations.SequenceEqual(other.Decorations, StringComparer.Ordinal) &&
            Actions.SequenceEqual(other.Actions, StringComparer.Ordinal) &&
            Occurrences.SequenceEqual(other.Occurrences, StringComparer.Ordinal) &&
            Angles.SequenceEqual(other.Angles) &&
            Positions.SequenceEqual(other.Positions) &&
            Bounds.Equals(other.Bounds);
    }
}
