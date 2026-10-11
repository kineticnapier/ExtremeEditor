using System.IO;
using System.Runtime.CompilerServices;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeScaleRadiusPlaybackRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        string source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp"));

        int start = source.IndexOf("PlaybackVisualState CalculatePlaybackVisual", StringComparison.Ordinal);
        int end = source.IndexOf("\n}\n\n}", start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            throw new InvalidOperationException("Native playback visual function layout changed; update this regression.");

        string body = source[start..end];
        if (body.Contains("* PlanetDistance", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native playback still uses fixed PlanetDistance for the orbit. ScaleRadius playback must derive orbit radius from the resolved current-to-next floor distance.");
        }

        if (!body.Contains("scene->floors[floor + 1", StringComparison.Ordinal) &&
            !body.Contains("scene->floors[next_floor", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native playback must read the resolved next floor position when calculating the orbit radius.");
        }
    }
}
