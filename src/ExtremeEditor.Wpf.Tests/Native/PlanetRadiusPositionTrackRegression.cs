using System.IO;
using System.Numerics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlanetRadiusPositionTrackRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 120;
        level.Positions =
        [
            Vector2.Zero,
            new Vector2(9.0f, 0.0f),
            new Vector2(10.5f, 0.0f)
        ];
        level.Bounds = PathBuilder.CalculateBounds(level.Positions);
        TimingMap timing = TimingMapBuilder.Build(level);
        FloorTiming first = timing.Floors[0];
        double sample = first.EntryTime + (first.ExitTime - first.EntryTime) * 0.5;
        float distance = Vector2.Distance(
            timing.GetPose(level, sample).StationaryPlanet,
            timing.GetPose(level, sample).OrbitingPlanet);
        if (Math.Abs(distance - PathBuilder.DefaultLongTileSize) > 0.0001f)
        {
            failures.Add(
                $"PositionTrack changed planet radius to {distance}; expected default {PathBuilder.DefaultLongTileSize}. The orbit should remain small and teleport on tile transition.");
        }

        string root = FindRepositoryRoot();
        string native = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.NativeRenderer", "src", "playback_visual.h"));
        int begin = native.IndexOf("PlaybackVisualState CalculatePlaybackVisual", StringComparison.Ordinal);
        string function = begin < 0 ? native : native[begin..];
        if (function.Contains("orbit_distance = resolved_distance;", StringComparison.Ordinal))
        {
            failures.Add("Native playback still derives planet radius from displaced tile coordinates.");
        }

        if (failures.Count != 0)
            throw new InvalidOperationException("Planet radius / PositionTrack separation RED:\n- " +
                string.Join("\n- ", failures));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
