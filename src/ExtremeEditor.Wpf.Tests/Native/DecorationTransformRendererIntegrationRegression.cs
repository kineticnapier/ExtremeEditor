using System.Collections;
using System.IO;
using System.Reflection;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class DecorationTransformRendererIntegrationRegression
{
    private const double Tolerance = 0.0001;
    private const string MissingIntegration =
        "Renderer-facing decoration transform semantics are incomplete.";

    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-DecorationTransform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string chartPath = Path.Combine(directory, "transform-integration.adofai");
            WriteOnePixelPng(Path.Combine(directory, "decoration.png"));
            File.WriteAllText(chartPath, CreateFixture());

            LevelDocument level = AdoFaiLoader.LoadFlatAsync(chartPath).GetAwaiter().GetResult().Document;
            VfxTimeline timeline = VfxTimelineBuilder.Build(level);
            double playbackTime = timeline.Occurrences
                .Single(item => item.EventType == "MoveDecorations" && item.SourceIndex == 0)
                .StartTime;

            IReadOnlyList<object> items = BuildSnapshot(level, timeline, playbackTime);
            if (items.Count != 6)
            {
                throw new InvalidOperationException(
                    $"{MissingIntegration} Expected Tile, Camera, CameraAspect and three planet instances; actual={items.Count}.");
            }

            object tile = Find(items, 0);
            AssertNear(ReadSingle(tile, "PositionX"), 4.5, "Tile X did not apply tileSize once.");
            AssertNear(ReadSingle(tile, "PositionY"), 1.5, "Tile Y did not apply tileSize once.");
            AssertNear(ReadSingle(tile, "ParallaxOffsetX"), 4.5, "parallaxOffset X was lost or not converted to world units.");
            AssertNear(ReadSingle(tile, "ParallaxOffsetY"), 6.0, "parallaxOffset Y was lost or not converted to world units.");
            AssertNear(ReadSingle(tile, "ScaleMultiplier"), 1.25, "scaleMultiplier was lost.");
            AssertFlag(tile, "FlagStickToFloor", "stickToFloor was lost.");
            AssertFlag(tile, "FlagLockRotation", "lockRotation was lost.");
            AssertFlag(tile, "FlagLockScale", "lockScale was lost.");

            AssertPlacement(Find(items, 1), "Camera");
            AssertPlacement(Find(items, 2), "CameraAspect");
            AssertPlacement(Find(items, 3), "RedPlanet");
            AssertPlacement(Find(items, 4), "BluePlanet");
            AssertPlacement(Find(items, 5), "GreenPlanet");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<object> BuildSnapshot(
        LevelDocument level,
        VfxTimeline timeline,
        double playbackTime)
    {
        Type? builder = typeof(ExtremeEditor.Wpf.MainWindow).Assembly.GetType(
            "ExtremeEditor.Wpf.Native.StaticDecorationSnapshotBuilder");
        MethodInfo? build = builder?.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(LevelDocument), typeof(VfxTimeline), typeof(double)],
            modifiers: null);
        object? snapshot = build?.Invoke(null, [level, timeline, playbackTime]);
        PropertyInfo? instancesProperty = snapshot?.GetType().GetProperty("Instances");
        if (instancesProperty?.GetValue(snapshot) is not IEnumerable instances)
            throw new InvalidOperationException(MissingIntegration);
        return instances.Cast<object>().ToArray();
    }

    private static object Find(IEnumerable<object> items, int sourceIndex) =>
        items.Single(item => ReadInt32(item, "SourceIndex") == sourceIndex);

    private static void AssertPlacement(object item, string expected)
    {
        PropertyInfo? property = item.GetType().GetProperty("RelativeTo");
        string? actual = property?.GetValue(item) as string;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{MissingIntegration} Expected placement {expected}; actual={actual ?? "<missing>"}.");
        }
    }

    private static void AssertFlag(object item, string flagName, string message)
    {
        Type type = item.GetType();
        FieldInfo? flagField = type.GetField(flagName, BindingFlags.Public | BindingFlags.Static);
        FieldInfo? flagsField = type.GetField("Flags", BindingFlags.Public | BindingFlags.Instance);
        if (flagField?.GetValue(null) is not uint flag ||
            flagsField?.GetValue(item) is not uint flags ||
            (flags & flag) == 0)
        {
            throw new InvalidOperationException($"{MissingIntegration} {message}");
        }
    }

    private static float ReadSingle(object item, string fieldName)
    {
        FieldInfo? field = item.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(item) is float value
            ? value
            : throw new InvalidOperationException($"{MissingIntegration} Missing {fieldName}.");
    }

    private static int ReadInt32(object item, string fieldName)
    {
        FieldInfo? field = item.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(item) is int value
            ? value
            : throw new InvalidOperationException($"{MissingIntegration} Missing {fieldName}.");
    }

    private static void AssertNear(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > Tolerance)
            throw new InvalidOperationException($"{MissingIntegration} {message} expected={expected} actual={actual}.");
    }

    private static string CreateFixture() =>
        """
        {
          "angleData": [0, 0],
          "settings": { "bpm": 60, "pitch": 100 },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveDecorations",
              "active": true,
              "duration": 0,
              "tag": "tile",
              "relativeTo": "Global",
              "positionOffset": [2, -1],
              "parallaxOffset": [3, 4]
            }
          ],
          "decorations": [
            {
              "floor": 1,
              "eventType": "AddDecoration",
              "decorationImage": "decoration.png",
              "tag": "tile",
              "relativeTo": "Tile",
              "position": [1, 2],
              "pivotOffset": [0.25, -0.5],
              "scale": [100, 100],
              "opacity": 100,
              "parallax": [50, 25],
              "stickToFloor": true,
              "lockRotation": true,
              "lockScale": true,
              "scaleMultiplier": 1.25
            },
            { "eventType": "AddDecoration", "decorationImage": "decoration.png", "relativeTo": "Camera", "position": [1, 2] },
            { "eventType": "AddDecoration", "decorationImage": "decoration.png", "relativeTo": "CameraAspect", "position": [1, 2] },
            { "floor": 1, "eventType": "AddDecoration", "decorationImage": "decoration.png", "relativeTo": "RedPlanet", "position": [1, 2] },
            { "floor": 1, "eventType": "AddDecoration", "decorationImage": "decoration.png", "relativeTo": "BluePlanet", "position": [1, 2] },
            { "floor": 1, "eventType": "AddDecoration", "decorationImage": "decoration.png", "relativeTo": "GreenPlanet", "position": [1, 2] }
          ]
        }
        """;

    private static void WriteOnePixelPng(string path) => File.WriteAllBytes(
        path,
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgAI/3pKMVwAAAABJRU5ErkJggg=="));
}
