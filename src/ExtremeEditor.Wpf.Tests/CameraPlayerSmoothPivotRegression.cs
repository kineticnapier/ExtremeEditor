using System.IO;
using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraPlayerSmoothPivotRegression
{
    public static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-CameraPlayerSmoothPivot-{Guid.NewGuid():N}.adofai");

        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 90],
              "settings": {
                "bpm": 60,
                "offset": 0,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "relativeTo": "Global",
                "position": [0, 0],
                "rotation": 0,
                "zoom": 100
              },
              "actions": []
            }
            """);

            WpfLevelLoadResult loaded = WpfLevelLoader.Load(path);
            LevelDocument level = loaded.Document;
            if (level.Positions.Length < 2)
                throw new InvalidOperationException("RED setup: expected at least two floor positions.");

            // At 60 BPM ADOFAI's normal follow camera takes 2 beats = 2 seconds.
            // Floor 1 is hit at t=1, so at t=2 the smooth Player pivot must be
            // halfway from floor 0 to floor 1 even though the raw stationary
            // planet has already arrived at floor 1.
            var timing = new TimingMap(
            [
                new FloorTiming(0, 0.0, 1.0, 0, 180, 180, 60, false, false),
                new FloorTiming(1, 1.0, 3.0, 0, 180, 180, 60, false, false),
                new FloorTiming(2, 3.0, 4.0, 0, 180, 180, 60, false, false)
            ]);

            const double eventTime = 2.0;
            PlaybackPose rawPose = timing.GetPose(level, eventTime);
            Vector2 smoothPivot = Vector2.Lerp(level.Positions[0], level.Positions[1], 0.5f);

            if (Vector2.Distance(rawPose.StationaryPlanet, smoothPivot) <= 0.0001f)
            {
                throw new InvalidOperationException(
                    "RED setup: raw Player pivot must differ from the 2-beat smooth-follow pivot.");
            }

            const float worldStartX = 123.0f;
            const float worldStartY = -45.0f;
            NativeCameraEvent[] events =
            [
                new NativeCameraEvent
                {
                    StartTime = eventTime,
                    DurationSeconds = 4.0,
                    StartX = worldStartX,
                    StartY = worldStartY,
                    TargetX = 0.0f,
                    TargetY = 0.0f,
                    Flags = NativeCameraEvent.FlagApplyX |
                            NativeCameraEvent.FlagApplyY |
                            NativeCameraEvent.FlagTargetPlayerX |
                            NativeCameraEvent.FlagTargetPlayerY,
                    Ease = NativeCameraEvent.EaseLinear
                }
            ];

            NativeCameraRuntimeCompatibility.MakePlayerStartsRelative(level, timing, events);

            float expectedX = worldStartX - smoothPivot.X;
            float expectedY = worldStartY - smoothPivot.Y;
            NativeCameraEvent converted = events[0];
            if (Math.Abs(converted.StartX - expectedX) > 0.0001f ||
                Math.Abs(converted.StartY - expectedY) > 0.0001f)
            {
                throw new InvalidOperationException(
                    "RED: Player MoveCamera start must be rebased against the 2-beat smooth-follow pivot, not the raw stationary planet. " +
                    $"ExpectedLocal=({expectedX},{expectedY}), ActualLocal=({converted.StartX},{converted.StartY}), " +
                    $"RawPlayer=({rawPose.StationaryPlanet.X},{rawPose.StationaryPlanet.Y}), " +
                    $"SmoothPlayer=({smoothPivot.X},{smoothPivot.Y}).");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
