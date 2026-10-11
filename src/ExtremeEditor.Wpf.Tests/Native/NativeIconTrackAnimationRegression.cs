using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeIconTrackAnimationRegression
{
    internal static void Run()
    {
        string source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "icon_instanced_renderer.cpp"));

        if (!source.Contains("transform_scale_x", StringComparison.Ordinal) ||
            !source.Contains("transform_scale_y", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "native event icons do not inherit the animated floor scale.");
        }

        if (!source.Contains("transform_opacity", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "native event icons do not inherit the animated floor opacity.");
        }
    }
}
