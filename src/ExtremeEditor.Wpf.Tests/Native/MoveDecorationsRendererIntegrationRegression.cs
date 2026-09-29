using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsRendererIntegrationRegression
{
    private const double Tolerance = 0.0001;
    private const string MissingIntegration =
        "Renderer-facing decoration snapshot does not reflect MoveDecorations evaluation at playback time.";

    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-MoveDecorationRenderer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string chartPath = Path.Combine(directory, "renderer-integration.adofai");
            File.WriteAllText(chartPath, "{}");
            WriteOnePixelPng(Path.Combine(directory, "static.png"));
            WriteOnePixelPng(Path.Combine(directory, "animated.png"));

            LevelDocument level = CreateFixture(chartPath);
            VfxTimeline timeline = VfxTimelineBuilder.Build(level);
            VfxOccurrence move = timeline.Occurrences.Single(occurrence =>
                occurrence.SourceIndex == 0 &&
                string.Equals(occurrence.EventType, "MoveDecorations", StringComparison.Ordinal));
            double playbackTime = move.StartTime;

            IReadOnlyList<object> instances = RendererSnapshotContract.Build(
                level,
                timeline,
                playbackTime);
            if (instances.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Renderer-facing snapshot lost valid AddDecoration instances. expected=2 actual={instances.Count}.");
            }

            object staticItem = FindBySourceIndex(instances, 0);
            AssertItem(
                staticItem,
                position: (1, 2),
                pivot: (0.25, -0.5),
                rotationDegrees: 10,
                scale: (0.8, 1.2),
                opacity: 0.9,
                message: "A static decoration changed while building a playback-time snapshot.");

            LevelDecoration animatedDecoration = level.Decorations.Single(item => item.SourceIndex == 1);
            DecorationState expectedState = DecorationState.Evaluate(
                animatedDecoration,
                timeline,
                playbackTime);
            AssertState(expectedState);

            object animatedItem = FindBySourceIndex(instances, 1);
            AssertItem(
                animatedItem,
                position: (expectedState.PositionX, expectedState.PositionY),
                pivot: (expectedState.PivotOffsetX, expectedState.PivotOffsetY),
                rotationDegrees: expectedState.Rotation,
                scale: (expectedState.ScaleX / 100.0, expectedState.ScaleY / 100.0),
                opacity: expectedState.Opacity / 100.0,
                message: MissingIntegration);

            // The resolved world position is the oracle for the eventual renderer
            // transform. The ABI may carry chart-space state plus placement mode or
            // a pre-resolved position, so this regression does not dictate layout.
            var expectedWorld = DecorationWorldTransformResolver.ResolveWorldPosition(
                level,
                animatedDecoration,
                expectedState);
            if (!Near(expectedWorld.X, 9) || !Near(expectedWorld.Y, 3))
                throw new InvalidOperationException("MoveDecorations world-position oracle is invalid.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static LevelDocument CreateFixture(string chartPath)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.SourcePath = chartPath;
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations(
        [
            CreateDecoration(
                sourceIndex: 0,
                image: "static.png",
                tag: "static-only",
                position: (1, 2),
                pivot: (0.25, -0.5),
                rotation: 10,
                scale: (80, 120),
                opacity: 90),
            CreateDecoration(
                sourceIndex: 1,
                image: "animated.png",
                tag: "animated",
                position: (2, 3),
                pivot: (0, 0),
                rotation: 5,
                scale: (100, 100),
                opacity: 100)
        ]);
        level.ReplaceActions(
        [
            CreateMove(
                floor: 0,
                sourceIndex: 0,
                tag: "animated",
                angleOffset: 180,
                positionOffset: (4, -1),
                pivotOffset: (1.5, -0.25),
                rotation: 90,
                scale: (50, 150),
                opacity: 40)
        ]);
        return level;
    }

    private static LevelDecoration CreateDecoration(
        int sourceIndex,
        string image,
        string tag,
        (double X, double Y) position,
        (double X, double Y) pivot,
        double rotation,
        (double X, double Y) scale,
        double opacity) =>
        new(0, "AddDecoration")
        {
            SourceIndex = sourceIndex,
            Properties = new JsonObject
            {
                ["floor"] = 0,
                ["eventType"] = "AddDecoration",
                ["decorationImage"] = image,
                ["tag"] = tag,
                ["relativeTo"] = "Global",
                ["position"] = new JsonArray(position.X, position.Y),
                ["pivotOffset"] = new JsonArray(pivot.X, pivot.Y),
                ["rotation"] = rotation,
                ["scale"] = new JsonArray(scale.X, scale.Y),
                ["color"] = "FFFFFF",
                ["opacity"] = opacity,
                ["depth"] = 3,
                ["visible"] = true
            }
        };

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        (double X, double Y) positionOffset,
        (double X, double Y) pivotOffset,
        double rotation,
        (double X, double Y) scale,
        double opacity)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["angleOffset"] = angleOffset,
            ["duration"] = 0,
            ["ease"] = "Linear",
            ["tag"] = tag,
            ["relativeTo"] = "Global",
            ["positionOffset"] = new JsonArray(positionOffset.X, positionOffset.Y),
            ["pivotOffset"] = new JsonArray(pivotOffset.X, pivotOffset.Y),
            ["rotation"] = rotation,
            ["scale"] = new JsonArray(scale.X, scale.Y),
            ["opacity"] = opacity
        };
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static void AssertState(DecorationState state)
    {
        if (!Near(state.PositionX, 6) || !Near(state.PositionY, 2) ||
            !Near(state.PivotOffsetX, 1.5) || !Near(state.PivotOffsetY, -0.25) ||
            !Near(state.Rotation, 90) ||
            !Near(state.ScaleX, 50) || !Near(state.ScaleY, 150) ||
            !Near(state.Opacity, 40))
        {
            throw new InvalidOperationException(
                "MoveDecorations fixture did not produce the expected evaluated DecorationState.");
        }
    }

    private static object FindBySourceIndex(IReadOnlyList<object> instances, int sourceIndex) =>
        instances.SingleOrDefault(item => ReadInt(item, "SourceIndex") == sourceIndex)
        ?? throw new InvalidOperationException(
            $"Renderer-facing snapshot omitted decoration source index {sourceIndex}.");

    private static void AssertItem(
        object item,
        (double X, double Y) position,
        (double X, double Y) pivot,
        double rotationDegrees,
        (double X, double Y) scale,
        double opacity,
        string message)
    {
        bool matches =
            Near(ReadDouble(item, "PositionX"), position.X) &&
            Near(ReadDouble(item, "PositionY"), position.Y) &&
            Near(ReadDouble(item, "PivotOffsetX"), pivot.X) &&
            Near(ReadDouble(item, "PivotOffsetY"), pivot.Y) &&
            Near(ReadDouble(item, "RotationRadians"), rotationDegrees * Math.PI / 180.0) &&
            Near(ReadDouble(item, "ScaleX"), scale.X) &&
            Near(ReadDouble(item, "ScaleY"), scale.Y) &&
            Near(ReadDouble(item, "Opacity"), opacity);
        if (!matches)
            throw new InvalidOperationException(message);
    }

    private static int ReadInt(object source, string name) =>
        Convert.ToInt32(ReadRequired(source, name));

    private static double ReadDouble(object source, string name) =>
        Convert.ToDouble(ReadRequired(source, name));

    private static object ReadRequired(object source, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = source.GetType();
        return type.GetProperty(name, flags)?.GetValue(source) ??
               type.GetField(name, flags)?.GetValue(source) ??
               throw new InvalidOperationException($"Renderer decoration item does not expose {name}.");
    }

    private static bool Near(double actual, double expected) =>
        Math.Abs(actual - expected) <= Tolerance;

    private static void WriteOnePixelPng(string path)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/l1uHOwAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }

    private static class RendererSnapshotContract
    {
        public static IReadOnlyList<object> Build(
            LevelDocument level,
            VfxTimeline timeline,
            double timeSeconds)
        {
            object? result = TryBuildTimeAware(level, timeline, timeSeconds);
            if (result is null)
                return NativeLevelSnapshotBuilder.Build(level).StaticDecorations.Cast<object>().ToArray();

            if (result is NativeLevelSnapshot snapshot)
                return snapshot.StaticDecorations.Cast<object>().ToArray();
            if (result is IEnumerable enumerable && result is not string)
                return enumerable.Cast<object>().ToArray();

            object? instances = ReadOptional(result, "Instances", "StaticDecorations", "Decorations");
            return instances is IEnumerable sequence
                ? sequence.Cast<object>().ToArray()
                : throw new InvalidOperationException(
                    "Playback-time decoration snapshot did not expose renderer-facing instances.");
        }

        private static object? TryBuildTimeAware(
            LevelDocument level,
            VfxTimeline timeline,
            double timeSeconds)
        {
            Assembly assembly = typeof(NativeLevelSnapshotBuilder).Assembly;
            foreach (Type type in assembly.GetTypes().Where(type =>
                         type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase) &&
                         type.Name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name is not ("Build" or "BuildAtTime" or "Evaluate" or "Resolve"))
                        continue;
                    Type[] parameters = method.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .ToArray();
                    if (parameters.SequenceEqual(
                            [typeof(LevelDocument), typeof(VfxTimeline), typeof(double)]))
                    {
                        return method.Invoke(null, [level, timeline, timeSeconds]);
                    }
                    if (parameters.SequenceEqual([typeof(LevelDocument), typeof(double)]))
                        return method.Invoke(null, [level, timeSeconds]);
                }
            }
            return null;
        }

        private static object? ReadOptional(object source, params string[] names)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = source.GetType();
            foreach (string name in names)
            {
                object? value = type.GetProperty(name, flags)?.GetValue(source) ??
                                type.GetField(name, flags)?.GetValue(source);
                if (value is not null)
                    return value;
            }
            return null;
        }
    }
}
