using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsCameraPlacementRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingCameraTransform =
        "MoveDecorations Camera/CameraAspect placement does not match game screen-clamp semantics.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_CAMERA_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations camera placement regression is valid.");
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
        ScreenRelativePositionContract contract = ScreenRelativePositionContract.Discover();

        const float screenWidth = 200f;
        const float screenHeight = 100f;

        LevelDecoration camera = FindDecoration(level, 0);
        VfxOccurrence cameraMove = FindOccurrence(timeline, 0);
        DecorationState cameraState = DecorationState.Evaluate(camera, timeline, cameraMove.StartTime);
        AssertPair(cameraState.Position, (5, 0),
            "Camera placement leaked screen coordinates into logical DecorationState position.");

        Vector2 cameraRelative = contract.Resolve(camera, cameraState, screenWidth, screenHeight);
        AssertPair((cameraRelative.X, cameraRelative.Y), (0.8, 0.6), MissingCameraTransform);

        LevelDecoration aspect = FindDecoration(level, 1);
        VfxOccurrence aspectMove = FindOccurrence(timeline, 1);
        DecorationState aspectState = DecorationState.Evaluate(aspect, timeline, aspectMove.StartTime);
        AssertPair(aspectState.Position, (5, 0),
            "CameraAspect placement leaked screen coordinates into logical DecorationState position.");

        Vector2 aspectRelative = contract.Resolve(aspect, aspectState, screenWidth, screenHeight);
        AssertPair((aspectRelative.X, aspectRelative.Y), (0.65, 0.6),
            "CameraAspect did not apply Screen.height / Screen.width to X before screen normalization.");

        // Camera/CameraAspect use chart-space position and pivotOffset directly: SetPlacementType
        // cancels the initial tileSize multiplication, and Setup leaves pivotOffset unscaled.
        // The logical state must therefore remain unchanged by screen aspect or tile size.
        Vector2 aspectWide = contract.Resolve(aspect, aspectState, 400f, 100f);
        AssertPair((aspectWide.X, aspectWide.Y), (0.575, 0.6),
            "CameraAspect screen-relative X did not track the current viewport aspect ratio.");
        AssertPair(aspectState.Position, (5, 0),
            "CameraAspect resolver mutated DecorationState logical coordinates.");
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        level.ReplaceDecorations(
        [
            CreateDecoration(0, "camera", "Camera"),
            CreateDecoration(1, "aspect", "CameraAspect")
        ]);
        level.ReplaceActions(
        [
            CreateMove(1, 0, "camera"),
            CreateMove(1, 1, "aspect")
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
                ["position"] = new JsonArray(2, -4),
                ["pivotOffset"] = new JsonArray(1, 2),
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

    private sealed class ScreenRelativePositionContract
    {
        private readonly MethodInfo _method;

        private ScreenRelativePositionContract(MethodInfo method)
        {
            _method = method;
        }

        public static ScreenRelativePositionContract Discover()
        {
            Type[] parameterTypes =
            [
                typeof(LevelDecoration),
                typeof(DecorationState),
                typeof(float),
                typeof(float)
            ];

            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase))
                    continue;

                MethodInfo? method = type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(candidate =>
                        candidate.Name is "ResolveScreenRelativePosition" or "ToScreenRelativePosition" &&
                        candidate.ReturnType == typeof(Vector2) &&
                        candidate.GetParameters().Select(parameter => parameter.ParameterType)
                            .SequenceEqual(parameterTypes));
                if (method is not null)
                    return new ScreenRelativePositionContract(method);
            }

            throw new InvalidOperationException(MissingCameraTransform);
        }

        public Vector2 Resolve(
            LevelDecoration decoration,
            DecorationState state,
            float screenWidth,
            float screenHeight)
        {
            object? result = _method.Invoke(null, [decoration, state, screenWidth, screenHeight]);
            return result is Vector2 vector
                ? vector
                : throw new InvalidOperationException("Camera screen-relative resolver returned no Vector2.");
        }
    }
}
