using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraRuntimeSemanticsRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        VerifyPitchAdjustedDuration(failures);
        VerifyExplicitNullRelativeToIsDisabled(failures);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Camera runtime semantics mismatch:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyPitchAdjustedDuration(List<string> failures)
    {
        string path = WriteFixture("Pitch", """
        {
          "angleData": [0, 0],
          "settings": {
            "bpm": 60,
            "pitch": 200,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "relativeTo": "Global",
            "position": [0, 0],
            "rotation": 0,
            "zoom": 100
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveCamera",
              "active": true,
              "duration": 2,
              "relativeTo": "Global",
              "position": [1, 0],
              "angleOffset": 90,
              "ease": "Linear"
            }
          ]
        }
        """);

        try
        {
            LevelDocument level = WpfLevelLoader.Load(path).Document;
            var timing = new TimingMap(
            [
                new FloorTiming(0, 0.0, 10.0, 0, 180, 180, 120, false, false),
                new FloorTiming(1, 10.0, 11.0, 0, 180, 180, 120, false, false),
                new FloorTiming(2, 11.0, 12.0, 0, 180, 180, 120, false, false)
            ]);

            NativeCameraEvent item = FaithfulNativeCameraTimelineBuilder.Build(level, timing)
                .Single(candidate => candidate.StartTime > 0.0);

            // ffxPlusBase.SetStartTime excludes song.pitch:
            // 10 + 90 / 180 * 60 / 120 = 10.25.
            const double expectedStart = 10.25;
            if (Math.Abs(item.StartTime - expectedStart) > 1.0e-7)
            {
                failures.Add(
                    $"RED: MoveCamera trigger time must use floor speed/BPM but not song.pitch. " +
                    $"expected={expectedStart}, actual={item.StartTime}.");
            }

            // ffxCameraPlus.Decode multiplies duration by crotchet, where
            // crotchet = 60 / (bpm * song.pitch * floor.speed).
            // 2 beats * (60 / 120) / 2.0 pitch = 0.5 seconds.
            const double expectedDuration = 0.5;
            if (Math.Abs(item.DurationSeconds - expectedDuration) > 1.0e-7)
            {
                failures.Add(
                    $"RED: MoveCamera duration must include song.pitch independently of trigger time. " +
                    $"expected={expectedDuration}, actual={item.DurationSeconds}.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyExplicitNullRelativeToIsDisabled(List<string> failures)
    {
        string path = WriteFixture("NullRelativeTo", """
        {
          "angleData": [0, 0],
          "settings": {
            "bpm": 60,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "relativeTo": "Global",
            "position": [0, 0],
            "rotation": 0,
            "zoom": 100
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveCamera",
              "active": true,
              "duration": 1,
              "relativeTo": null,
              "position": [2, null],
              "rotation": null,
              "zoom": null,
              "angleOffset": 0,
              "ease": "Linear"
            }
          ]
        }
        """);

        try
        {
            LevelDocument level = WpfLevelLoader.Load(path).Document;
            var timing = new TimingMap(
            [
                new FloorTiming(0, 0.0, 1.0, 0, 180, 180, 60, false, false),
                new FloorTiming(1, 1.0, 2.0, 0, 180, 180, 60, false, false),
                new FloorTiming(2, 2.0, 3.0, 0, 180, 180, 60, false, false)
            ]);

            NativeCameraEvent item = FaithfulNativeCameraTimelineBuilder.Build(level, timing)
                .Single(candidate => candidate.StartTime > 0.0);

            bool playerRelative = (item.Flags &
                (NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY)) != 0u;
            bool appliesY = (item.Flags & NativeCameraEvent.FlagApplyY) != 0u;

            // LevelEvent.disabled["relativeTo"] is true for explicit null. Stock
            // therefore preserves the previous Global mode. The non-null X axis
            // remains a normal partial update; null Y remains untouched.
            if (playerRelative || appliesY)
            {
                failures.Add(
                    "RED: explicit null relativeTo must preserve the previous camera mode, " +
                    "and a null position axis must not become an active channel. " +
                    $"flags=0x{item.Flags:X8}.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteFixture(string name, string json)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-CameraRuntimeSemantics-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, json);
        return path;
    }
}
