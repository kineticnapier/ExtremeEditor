using System.Runtime.CompilerServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class ScaleRadiusSourceFallbackRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        var source = new ScaleRadiusSourceData(
        [
            new ScaleRadiusSourceEvent(
                SourceIndex: 17,
                Floor: 1,
                Active: true,
                Scale: 200.0,
                EditorOnly: false)
        ]);

        float[] scales = ScaleRadiusResolver.ResolveScales(level, source);

        float[] expected = [1.0f, 2.0f, 2.0f, 2.0f];
        if (scales.Length != expected.Length)
            throw new InvalidOperationException(
                $"ScaleRadius source fallback length changed: expected {expected.Length}, actual {scales.Length}.");

        for (int floor = 0; floor < expected.Length; floor++)
        {
            if (Math.Abs(scales[floor] - expected[floor]) > 0.0001f)
            {
                throw new InvalidOperationException(
                    $"ScaleRadius source metadata must affect geometry even when the flat core ActionStore does not retain the action. floor={floor} expected={expected[floor]} actual={scales[floor]}.");
            }
        }
    }
}
