using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsLockTransformRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingTransform =
        "MoveDecorations lockRotation/lockScale does not match game camera-lock semantics.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_LOCK_TRANSFORM_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations lock transform regression is valid.");
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
        LockTransformContract contract = LockTransformContract.Discover();

        LevelDecoration locked = CreateDecoration(lockRotation: true, lockScale: true, stickToFloor: false, scaleMultiplier: 1.25f);
        float rotation = contract.ResolveRotation(locked, baseRotation: 30f, parentFloorRotation: 70f, cameraRotation: -15f);
        AssertNear(rotation, 15f,
            "lockRotation did not add the current camera Z rotation when stickToFloor=false.");

        Vector2 scale = contract.ResolveScale(
            locked,
            new Vector2(1.2f, 0.8f),
            Vector2.One,
            cameraOrthographicSize: 8f,
            levelCameraZoomPercent: 100f);
        AssertPair(scale, new Vector2(2.4f, 1.6f),
            "lockScale did not apply camera zoom compensation and scaleMultiplier.");

        LevelDecoration stickWins = CreateDecoration(lockRotation: true, lockScale: true, stickToFloor: true, scaleMultiplier: 1.25f);
        float stickRotation = contract.ResolveRotation(stickWins, 30f, 70f, -15f);
        AssertNear(stickRotation, 100f,
            "stickToFloor rotation should take precedence over lockRotation camera rotation.");

        Vector2 stickScale = contract.ResolveScale(
            stickWins,
            new Vector2(1.2f, 0.8f),
            new Vector2(2f, 0.5f),
            cameraOrthographicSize: 8f,
            levelCameraZoomPercent: 100f);
        AssertPair(stickScale, new Vector2(4.8f, 0.8f),
            "stickToFloor scale should compound with lockScale camera compensation.");

        LevelDecoration unlocked = CreateDecoration(lockRotation: false, lockScale: false, stickToFloor: false, scaleMultiplier: 1.25f);
        AssertNear(contract.ResolveRotation(unlocked, 30f, 70f, -15f), 30f,
            "lockRotation=false unexpectedly applied camera or floor rotation.");
        AssertPair(
            contract.ResolveScale(unlocked, new Vector2(1.2f, 0.8f), Vector2.One, 8f, 100f),
            new Vector2(1.5f, 1f),
            "scaleMultiplier should apply independently of lockScale.");
    }

    private static LevelDecoration CreateDecoration(
        bool lockRotation,
        bool lockScale,
        bool stickToFloor,
        float scaleMultiplier) =>
        new(1, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["relativeTo"] = "Tile",
                ["lockRotation"] = lockRotation,
                ["lockScale"] = lockScale,
                ["stickToFloor"] = stickToFloor,
                ["scaleMultiplier"] = scaleMultiplier,
                ["position"] = new JsonArray(0, 0),
                ["rotation"] = 0,
                ["scale"] = new JsonArray(100, 100),
                ["opacity"] = 100
            }
        };

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

    private sealed class LockTransformContract
    {
        private readonly MethodInfo _rotationMethod;
        private readonly MethodInfo _scaleMethod;

        private LockTransformContract(MethodInfo rotationMethod, MethodInfo scaleMethod)
        {
            _rotationMethod = rotationMethod;
            _scaleMethod = scaleMethod;
        }

        public static LockTransformContract Discover()
        {
            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase))
                    continue;

                MethodInfo? rotation = type.GetMethod(
                    "ResolveDecorationRotation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: [typeof(LevelDecoration), typeof(float), typeof(float), typeof(float)],
                    modifiers: null);
                MethodInfo? scale = type.GetMethod(
                    "ResolveDecorationScale",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: [typeof(LevelDecoration), typeof(Vector2), typeof(Vector2), typeof(float), typeof(float)],
                    modifiers: null);

                if (rotation is not null && rotation.ReturnType == typeof(float) &&
                    scale is not null && scale.ReturnType == typeof(Vector2))
                {
                    return new LockTransformContract(rotation, scale);
                }
            }

            throw new InvalidOperationException(MissingTransform);
        }

        public float ResolveRotation(
            LevelDecoration decoration,
            float baseRotation,
            float parentFloorRotation,
            float cameraRotation)
        {
            object? result = _rotationMethod.Invoke(
                null,
                [decoration, baseRotation, parentFloorRotation, cameraRotation]);
            return result is float value
                ? value
                : throw new InvalidOperationException("Decoration rotation resolver returned no float.");
        }

        public Vector2 ResolveScale(
            LevelDecoration decoration,
            Vector2 baseScale,
            Vector2 parentFloorScale,
            float cameraOrthographicSize,
            float levelCameraZoomPercent)
        {
            object? result = _scaleMethod.Invoke(
                null,
                [decoration, baseScale, parentFloorScale, cameraOrthographicSize, levelCameraZoomPercent]);
            return result is Vector2 value
                ? value
                : throw new InvalidOperationException("Decoration scale resolver returned no Vector2.");
        }
    }
}
