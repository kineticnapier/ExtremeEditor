using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraPlayerDoubleRelativeRegression
{
    public static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-CameraPlayerDoubleRelative-{Guid.NewGuid():N}.adofai");

        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 0, 0, 0],
              "settings": {
                "bpm": 60,
                "offset": 0,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "relativeTo": "Global",
                "position": [4, 1],
                "rotation": 0,
                "zoom": 100
              },
              "actions": [
                {
                  "floor": 2,
                  "eventType": "MoveCamera",
                  "duration": 1,
                  "relativeTo": "Player",
                  "position": [0, 0],
                  "angleOffset": 0,
                  "ease": "Linear"
                },
                {
                  "floor": 3,
                  "eventType": "MoveCamera",
                  "duration": 1,
                  "position": [1, 0],
                  "angleOffset": 0,
                  "ease": "Linear"
                }
              ]
            }
            """);

            WpfLevelLoadResult loaded = WpfLevelLoader.Load(path);
            LevelDocument level = loaded.Document;
            var timing = new TimingMap(
            [
                new FloorTiming(0, -1.0, 0.0, 0, 180, 180, 60, false, false),
                new FloorTiming(1,  0.0, 1.0, 0, 180, 180, 60, false, false),
                new FloorTiming(2,  1.0, 2.0, 0, 180, 180, 60, false, false),
                new FloorTiming(3,  2.0, 3.0, 0, 180, 180, 60, false, false),
                new FloorTiming(4,  3.0, 4.0, 0, 180, 180, 60, false, false)
            ]);

            NativeCameraEvent[] events = FaithfulNativeCameraTimelineBuilder.Build(level, timing);

            int firstPlayerIndex = Array.FindIndex(events, item =>
                Math.Abs(item.StartTime - 1.0) <= 1.0e-7 &&
                (item.Flags & NativeCameraEvent.FlagTargetPlayerX) != 0u);
            if (firstPlayerIndex < 0)
                throw new InvalidOperationException("RED setup: expected Global -> Player MoveCamera event was not built.");

            int secondPlayerIndex = Array.FindIndex(events, item =>
                Math.Abs(item.StartTime - 2.0) <= 1.0e-7 &&
                (item.Flags & NativeCameraEvent.FlagTargetPlayerX) != 0u);
            if (secondPlayerIndex < 0)
                throw new InvalidOperationException("RED setup: expected subsequent Player MoveCamera event was not built.");

            NativeCameraEvent firstBefore = events[firstPlayerIndex];
            NativeCameraEvent secondBefore = events[secondPlayerIndex];
            PlaybackPose firstPose = timing.GetPose(level, firstBefore.StartTime);
            PlaybackPose secondPose = timing.GetPose(level, secondBefore.StartTime);

            if (Math.Abs(firstPose.StationaryPlanet.X) <= 0.0001f &&
                Math.Abs(firstPose.StationaryPlanet.Y) <= 0.0001f)
            {
                throw new InvalidOperationException(
                    "RED setup: Player pivot must be non-zero so duplicate relative conversion is observable.");
            }

            NativeCameraRuntimeCompatibility.MakePlayerStartsRelative(level, timing, events);

            NativeCameraEvent firstAfter = events[firstPlayerIndex];
            NativeCameraEvent secondAfter = events[secondPlayerIndex];

            AssertWorldStartPreserved(
                "Global -> Player",
                firstBefore,
                firstAfter,
                firstPose);
            AssertWorldStartPreserved(
                "Player -> Player",
                secondBefore,
                secondAfter,
                secondPose);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void AssertWorldStartPreserved(
        string transition,
        NativeCameraEvent before,
        NativeCameraEvent after,
        PlaybackPose pose)
    {
        if (Math.Abs(after.StartX - before.StartX) > 0.0001f ||
            Math.Abs(after.StartY - before.StartY) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"RED: {transition} Player MoveCamera start is already world-space in the faithful builder and must not be made relative again. " +
                $"Before=({before.StartX},{before.StartY}), After=({after.StartX},{after.StartY}), " +
                $"Player=({pose.StationaryPlanet.X},{pose.StationaryPlanet.Y}).");
        }
    }
}
