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
        if (!body.Contains("orbit_distance = DefaultPlanetDistance;", StringComparison.Ordinal))
            throw new InvalidOperationException("Native playback must use constant planet orbit radius.");
        if (body.Contains("orbit_distance = resolved_distance;", StringComparison.Ordinal) ||
            body.Contains("std::hypot(dx, dy)", StringComparison.Ordinal))
            throw new InvalidOperationException("Native playback must ignore displaced floor geometry for planet orbit radius.");
    }
}
