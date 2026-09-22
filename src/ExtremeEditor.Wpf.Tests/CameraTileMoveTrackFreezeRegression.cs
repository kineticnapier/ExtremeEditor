using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraTileMoveTrackFreezeRegression
{
    public static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-CameraTileMoveTrack-{Guid.NewGuid():N}.adofai");

        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 0, 0],
              "settings": {
                "bpm": 60,
                "offset": 0,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "relativeTo": "Player",
                "position": [0, 0],
                "rotation": 0,
                "zoom": 100
              },
              "actions": [
                {
                  "floor": 1,
                  "eventType": "MoveTrack",
                  "startTile": [0, "ThisTile"],
                  "endTile": [0, "ThisTile"],
                  "gapLength": 0,
                  "duration": 2,
                  "positionOffset": [10, 0],
                  "angleOffset": 0,
                  "ease": "Linear"
                },
                {
                  "floor": 1,
                  "eventType": "MoveCamera",
                  "duration": 1,
                  "relativeTo": "Tile",
                  "position": [0, 0],
                  "rotation": 0,
                  "zoom": 100,
                  "angleOffset": 180,
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
                new FloorTiming(1,  0.0, 2.0, 0, 180, 180, 60, false, false),
                new FloorTiming(2,  2.0, 3.0, 0, 180, 180, 60, false, false),
                new FloorTiming(3,  3.0, 4.0, 0, 180, 180, 60, false, false)
            ]);

            StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
            NativeTrackTransformEvent[] moveTimeline = TrackTransformResolver.BuildMoveTimeline(
                level,
                timing,
                staticTransforms);

            NativeTrackTransformEvent move = moveTimeline.Single(item =>
                item.Floor == 1 &&
                (item.Flags & NativeTrackTransformEvent.FlagX) != 0u);

            const double cameraStartTime = 1.0;
            float progress = (float)Math.Clamp(
                (cameraStartTime - move.StartTime) / move.DurationSeconds,
                0.0,
                1.0);
            float expectedFloorX = move.StartX + (move.TargetX - move.StartX) * progress;

            NativeCameraEvent[] cameraTimeline = FaithfulNativeCameraTimelineBuilder.Build(level, timing);
            NativeCameraEvent camera = cameraTimeline.Single(item =>
                Math.Abs(item.StartTime - cameraStartTime) <= 1.0e-7 &&
                (item.Flags & NativeCameraEvent.FlagApplyX) != 0u);

            if (Math.Abs(camera.TargetX - expectedFloorX) > 0.0001f)
            {
                throw new InvalidOperationException(
                    $"RED: relativeTo=Tile must capture floor.transform.position at MoveCamera StartEffect time. " +
                    $"MoveTrack midpoint floor X={expectedFloorX}, camera target X={camera.TargetX}, " +
                    $"static floor X={level.Positions[1].X}.");
            }

            if (Math.Abs(camera.TargetX - level.Positions[1].X) <= 0.0001f)
            {
                throw new InvalidOperationException(
                    "RED: Tile MoveCamera target incorrectly uses the static LevelDocument position instead of the in-progress MoveTrack position.");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
