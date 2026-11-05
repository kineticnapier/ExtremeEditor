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
        AssertPair(cameraState.Position, (6.5, 2),
            "Camera MoveDecorations positionOffset did not apply stock tileSize 1.5.");
        AssertPair(cameraState.PivotOffset, (4.5, 6),
            "Camera MoveDecorations pivotOffset did not apply stock tileSize 1.5.");

        Vector2 cameraRelative = contract.Resolve(camera, cameraState, screenWidth, screenHeight);
        AssertPair((cameraRelative.X, cameraRelative.Y), (1.05, 0.9), MissingCameraTransform);

        LevelDecoration aspect = FindDecoration(level, 1);
        VfxOccurrence aspectMove = FindOccurrence(timeline, 1);
        DecorationState aspectState = DecorationState.Evaluate(aspect, timeline, aspectMove.StartTime);
        AssertPair(aspectState.Position, (6.5, 2),
            "CameraAspect MoveDecorations positionOffset did not apply stock tileSize 1.5.");
        AssertPair(aspectState.PivotOffset, (4.5, 6),
            "CameraAspect MoveDecorations pivotOffset did not apply stock tileSize 1.5.");

        Vector2 aspectRelative = contract.Resolve(aspect, aspectState, screenWidth, screenHeight);
        AssertPair((aspectRelative.X, aspectRelative.Y), (0.775, 0.9),
            "CameraAspect did not apply Screen.height / Screen.width to X before screen normalization.");

        // AddDecoration starts in unscaled chart coordinates. Only MoveDecorations
        // positionOffset/pivotOffset use scrController.tileSize (1.5).
        Vector2 aspectWide = contract.Resolve(aspect, aspectState, 400f, 100f);
        AssertPair((aspectWide.X, aspectWide.Y), (0.6375, 0.9),
            "CameraAspect screen-relative X did not track the current viewport aspect ratio.");
        AssertPair(aspectState.Position, (6.5, 2),
            "CameraAspect resolver mutated DecorationState logical coordinates.");

        VerifyLastPositionScalesOnlyAddedOffset();
        VerifyTileAndGlobalRemainUnscaled();
        VerifyPhantigmaFloor2135(contract);
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
            ["positionOffset"] = new JsonArray(3, 4),
            ["pivotOffset"] = new JsonArray(3, 4)
        };
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = 0,
            Duration = 0,
            PropertyOverrides = properties
        };
    }

    private static void VerifyLastPositionScalesOnlyAddedOffset()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration(10, "last", "Camera");
        level.ReplaceDecorations([decoration]);
        LevelAction first = CreateMove(1, 10, "last");
        LevelAction second = CreateMove(2, 11, "last");
        second.PropertyOverrides!["relativeTo"] = "LastPosition";
        second.PropertyOverrides["positionOffset"] = new JsonArray(2, -2);
        second.PropertyOverrides.Remove("pivotOffset");
        level.ReplaceActions([first, second]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence last = FindOccurrence(timeline, 11);
        DecorationState state = DecorationState.Evaluate(decoration, timeline, last.StartTime);
        AssertPair(state.Position, (9.5, -1),
            "LastPosition must retain the captured base and scale only its added offset by tileSize.");
    }

    private static void VerifyTileAndGlobalRemainUnscaled()
    {
        foreach (string placement in new[] { "Tile", "Global" })
        {
            LevelDocument level = LevelDocument.CreateSynthetic(2);
            level.InitialBpm = 60;
            level.PitchPercent = 100;
            LevelDecoration decoration = CreateDecoration(20, placement, placement);
            level.ReplaceDecorations([decoration]);
            LevelAction move = CreateMove(1, 20, placement);
            level.ReplaceActions([move]);
            VfxTimeline timeline = VfxTimelineBuilder.Build(level);
            DecorationState state = DecorationState.Evaluate(
                decoration, timeline, FindOccurrence(timeline, 20).StartTime);
            AssertPair(state.Position, (5, 0),
                $"{placement} MoveDecorations positionOffset unexpectedly changed semantics.");
            AssertPair(state.PivotOffset, (3, 4),
                $"{placement} MoveDecorations pivotOffset unexpectedly changed semantics.");
        }
    }

    private static void VerifyPhantigmaFloor2135(ScreenRelativePositionContract contract)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration(30, "square", "Camera");
        decoration.Properties["position"] = new JsonArray(0, 0);
        decoration.Properties["pivotOffset"] = new JsonArray(0, 0);
        decoration.Properties["scale"] = new JsonArray(2000, 1000);
        decoration.Properties["lockScale"] = true;
        level.ReplaceDecorations([decoration]);
        LevelAction move = CreateMove(1, 30, "square");
        move.PropertyOverrides!["positionOffset"] = new JsonArray(-17, 0);
        move.PropertyOverrides.Remove("pivotOffset");
        level.ReplaceActions([move]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        DecorationState state = DecorationState.Evaluate(
            decoration, timeline, FindOccurrence(timeline, 30).StartTime);
        AssertPair(state.Position, (-25.5, 0),
            "Phantigma square positionOffset did not apply tileSize.");
        Vector2 relative = contract.Resolve(decoration, state, 960, 540);
        AssertPair((relative.X, relative.Y), (-0.775, 0.5),
            "Phantigma square center did not move beyond the left viewport edge.");
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
