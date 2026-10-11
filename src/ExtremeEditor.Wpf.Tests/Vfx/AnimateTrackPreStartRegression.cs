using System.IO;
using System.Runtime.CompilerServices;

namespace ExtremeEditor.Wpf.Tests;

internal static class AnimateTrackPreStartRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        string source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "track_animation_runtime.cpp"));

        int start = source.IndexOf("const double appear_start", StringComparison.Ordinal);
        int elapsed = source.IndexOf("const double elapsed = chart_time - appear_start;", start, StringComparison.Ordinal);
        if (start < 0 || elapsed < 0)
            throw new InvalidOperationException("AnimateTrack appear evaluation layout changed; update this regression.");

        string preStartSection = source[start..elapsed];
        if (!preStartSection.Contains("chart_time < appear_start", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AnimateTrack applies its initial hidden/offset state before the appear animation has actually started. Floors must remain in their resolved base state until appear_start.");
        }
    }
}
