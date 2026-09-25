using System.IO;
using System.Numerics;
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
            Vector2 firstPivot = FollowCameraPivotSampler.Evaluate(level, timing, firstBefore.StartTime);
            Vector2 secondPivot = FollowCameraPivotSampler.Evaluate(level, timing, secondBefore.StartTime);

            NativeCameraRuntimeCompatibility.MakePlayerStartsRelative(level, timing, events);

            NativeCameraEvent firstAfter = events[firstPlayerIndex];
            NativeCameraEvent secondAfter = events[secondPlayerIndex];

            AssertConvertedExactlyOnce(
                "Global -> Player",
                firstBefore,
                firstAfter,
                firstPivot);
            AssertConvertedExactlyOnce(
                "Player -> Player",
                secondBefore,
                secondAfter,
                secondPivot);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void AssertConvertedExactlyOnce(
        string transition,
        NativeCameraEvent before,
        NativeCameraEvent after,
        Vector2 smoothPivot)
    {
        float expectedX = before.StartX - smoothPivot.X;
        float expectedY = before.StartY - smoothPivot.Y;

        if (Math.Abs(after.StartX - expectedX) > 0.0001f ||
            Math.Abs(after.StartY - expectedY) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"RED: {transition} Player MoveCamera start must be converted from StartEffect-time world space to smooth-follow Player-local exactly once. " +
                $"Before=({before.StartX},{before.StartY}), After=({after.StartX},{after.StartY}), " +
                $"Expected=({expectedX},{expectedY}), SmoothPlayer=({smoothPivot.X},{smoothPivot.Y}).");
        }

        float reconstructedWorldX = after.StartX + smoothPivot.X;
        float reconstructedWorldY = after.StartY + smoothPivot.Y;
        if (Math.Abs(reconstructedWorldX - before.StartX) > 0.0001f ||
            Math.Abs(reconstructedWorldY - before.StartY) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"RED: {transition} smooth Player-local start must reconstruct the original world camera at StartEffect.");
        }
    }
}
