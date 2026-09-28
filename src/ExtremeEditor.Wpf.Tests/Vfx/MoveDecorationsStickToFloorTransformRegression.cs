using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsStickToFloorTransformRegression
{
    private const double Tolerance = 1e-6;
    private const string MissingTransform =
        "MoveDecorations stickToFloor rotation/scale does not match game floor-transform semantics.";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_STICK_TRANSFORM_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: MoveDecorations stickToFloor transform regression is valid.");
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
        LevelDecoration enabled = CreateDecoration(stickToFloor: true);
        LevelDecoration disabled = CreateDecoration(stickToFloor: false);
        StickTransformContract contract = StickTransformContract.Discover();

        const float baseRotation = 35f;
        const float parentRotation = 70f;
        float resolvedRotation = contract.ResolveRotation(enabled, baseRotation, parentRotation);
        AssertNear(resolvedRotation, 105f,
            "stickToFloor rotation did not add the current parent-floor Z rotation.");

        float disabledRotation = contract.ResolveRotation(disabled, baseRotation, parentRotation);
        AssertNear(disabledRotation, baseRotation,
            "stickToFloor=false unexpectedly changed decoration rotation.");

        Vector2 baseScale = new(1.25f, 0.8f);
        Vector2 parentScale = new(2f, 0.5f);
        Vector2 resolvedScale = contract.ResolveScale(enabled, baseScale, parentScale);
        AssertPair(resolvedScale, new Vector2(2.5f, 0.4f),
            "stickToFloor scale did not multiply by the current parent-floor local scale.");

        Vector2 disabledScale = contract.ResolveScale(disabled, baseScale, parentScale);
        AssertPair(disabledScale, baseScale,
            "stickToFloor=false unexpectedly changed decoration scale.");

        // The floor transform is dynamic. Re-evaluation with a different floor transform must
        // change only the final renderer transform, without altering the supplied base values.
        float movedRotation = contract.ResolveRotation(enabled, baseRotation, -20f);
        AssertNear(movedRotation, 15f,
            "stickToFloor rotation did not follow the current parent-floor rotation dynamically.");

        Vector2 movedScale = contract.ResolveScale(enabled, baseScale, new Vector2(0.5f, 3f));
        AssertPair(movedScale, new Vector2(0.625f, 2.4f),
            "stickToFloor scale did not follow the current parent-floor local scale dynamically.");
        AssertNear(baseRotation, 35f, "stickToFloor rotation resolver mutated the base rotation.");
        AssertPair(baseScale, new Vector2(1.25f, 0.8f),
            "stickToFloor scale resolver mutated the base scale.");
    }

    private static LevelDecoration CreateDecoration(bool stickToFloor) =>
        new(1, "AddDecoration")
        {
            SourceIndex = 0,
            Properties = new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["relativeTo"] = "Tile",
                ["stickToFloor"] = stickToFloor,
                ["position"] = new JsonArray(0, 0),
                ["rotation"] = 10,
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

    private sealed class StickTransformContract
    {
        private readonly MethodInfo _rotationMethod;
        private readonly MethodInfo _scaleMethod;

        private StickTransformContract(MethodInfo rotationMethod, MethodInfo scaleMethod)
        {
            _rotationMethod = rotationMethod;
            _scaleMethod = scaleMethod;
        }

        public static StickTransformContract Discover()
        {
            foreach (Type type in typeof(LevelDocument).Assembly.GetTypes())
            {
                if (!type.Name.Contains("Decoration", StringComparison.OrdinalIgnoreCase))
                    continue;

                MethodInfo? rotation = type.GetMethod(
                    "ResolveStickToFloorRotation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: [typeof(LevelDecoration), typeof(float), typeof(float)],
                    modifiers: null);
                MethodInfo? scale = type.GetMethod(
                    "ResolveStickToFloorScale",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: [typeof(LevelDecoration), typeof(Vector2), typeof(Vector2)],
                    modifiers: null);

                if (rotation is not null && rotation.ReturnType == typeof(float) &&
                    scale is not null && scale.ReturnType == typeof(Vector2))
                {
                    return new StickTransformContract(rotation, scale);
                }
            }

            throw new InvalidOperationException(MissingTransform);
        }

        public float ResolveRotation(LevelDecoration decoration, float baseRotation, float parentRotation)
        {
            object? result = _rotationMethod.Invoke(null, [decoration, baseRotation, parentRotation]);
            return result is float value
                ? value
                : throw new InvalidOperationException("stickToFloor rotation resolver returned no float.");
        }

        public Vector2 ResolveScale(LevelDecoration decoration, Vector2 baseScale, Vector2 parentScale)
        {
            object? result = _scaleMethod.Invoke(null, [decoration, baseScale, parentScale]);
            return result is Vector2 value
                ? value
                : throw new InvalidOperationException("stickToFloor scale resolver returned no Vector2.");
        }
    }
}
