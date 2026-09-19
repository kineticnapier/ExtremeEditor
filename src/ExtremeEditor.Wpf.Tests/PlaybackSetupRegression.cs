using System.IO;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackSetupRegression
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-WpfPlayback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string levelPath = Path.Combine(directory, "level.adofai");
        string songPath = Path.Combine(directory, "song.ogg");

        try
        {
            File.WriteAllText(levelPath, """
            {
              "angleData": [0, 90, 180],
              "settings": {
                "bpm": 120,
                "songFilename": "song.ogg",
                "offset": 250,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "hitsound": "Kick",
                "hitsoundVolume": 100
              },
              "actions": [
                { "floor": 1, "eventType": "SetHitsound", "hitsound": "Hat", "hitsoundVolume": 50 }
              ]
            }
            """);
            File.WriteAllBytes(songPath, [0]);

            LevelDocument level = AdoFaiLoader.Load(levelPath).Document;
            Assembly assembly = typeof(LevelViewport).Assembly;
            Type builderType = assembly.GetType("ExtremeEditor.Wpf.WpfPlaybackSetupBuilder")
                ?? throw new InvalidOperationException("WpfPlaybackSetupBuilder does not exist yet.");
            MethodInfo build = builderType.GetMethod(
                "Build",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WpfPlaybackSetupBuilder.Build is missing.");

            object setup = build.Invoke(null, [level])
                ?? throw new InvalidOperationException("WpfPlaybackSetupBuilder.Build returned null.");
            PropertyInfo timingProperty = setup.GetType().GetProperty("TimingMap")
                ?? throw new InvalidOperationException("WPF playback setup must expose TimingMap.");
            PropertyInfo hitSoundProperty = setup.GetType().GetProperty("HitSoundTimeline")
                ?? throw new InvalidOperationException("WPF playback setup must expose HitSoundTimeline.");
            PropertyInfo songPathProperty = setup.GetType().GetProperty("SongPath")
                ?? throw new InvalidOperationException("WPF playback setup must expose SongPath.");

            if (timingProperty.GetValue(setup) is not TimingMap timing)
                throw new InvalidOperationException("WPF playback setup TimingMap is invalid.");
            if (hitSoundProperty.GetValue(setup) is not HitSoundTimeline hitSounds)
                throw new InvalidOperationException("WPF playback setup HitSoundTimeline is invalid.");
            if (songPathProperty.GetValue(setup) is not string resolvedSongPath ||
                !string.Equals(Path.GetFullPath(resolvedSongPath), Path.GetFullPath(songPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WPF playback setup must resolve the chart song path.");
            }

            if (timing.Floors.Count != level.FloorCount)
                throw new InvalidOperationException("WPF playback setup timing map floor count mismatch.");

            HitSoundState state = hitSounds.GetStateAtFloor(1);
            if (!string.Equals(state.Name, "Hat", StringComparison.Ordinal) || Math.Abs(state.Volume - 0.5) > 0.000001)
                throw new InvalidOperationException("WPF playback setup must preserve the hitsound timeline.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
