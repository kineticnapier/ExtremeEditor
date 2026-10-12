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
            "src", "ExtremeEditor.NativeRenderer", "src", "playback_visual.h"));

        int start = source.IndexOf(
            "PlaybackVisualState CalculatePlaybackVisual",
            StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                "Shared native playback visual function layout changed; update this regression.");
        }

        string body = source[start..];
        if (body.Contains("* PlanetDistance", StringComparison.Ordinal) ||
            body.Contains("* DefaultPlanetDistance", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native playback still uses a fixed planet distance for the orbit. ScaleRadius playback must derive orbit radius from the resolved current-to-next floor distance.");
        }

        if (!body.Contains("scene->floors[floor + 1", StringComparison.Ordinal) &&
            !body.Contains("scene->floors[next_floor", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native playback must read the resolved next floor position when calculating the orbit radius.");
        }

        if (!body.Contains("hypot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native playback must derive the orbit radius from the resolved floor-to-floor distance.");
        }
    }
}
