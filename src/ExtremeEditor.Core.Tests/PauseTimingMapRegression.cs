using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class PauseTimingMapRegression
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    public static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-pause-{Guid.NewGuid():N}.adofai");
        string baselinePath = Path.Combine(Path.GetTempPath(), $"extremeeditor-pause-baseline-{Guid.NewGuid():N}.adofai");
        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 0, 0],
              "settings": {
                "bpm": 120,
                "offset": 0,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "hitsound": "Kick",
                "hitsoundVolume": 100
              },
              "actions": [
                {
                  "floor": 1,
                  "eventType": "Pause",
                  "duration": 2
                }
              ]
            }
            """);

            File.WriteAllText(baselinePath, """
            {
              "angleData": [0, 0, 0],
              "settings": {
                "bpm": 120,
                "offset": 0,
                "pitch": 100,
                "countdownTicks": 0,
                "separateCountdownTime": false,
                "hitsound": "Kick",
                "hitsoundVolume": 100
              },
              "actions": []
            }
            """);

            LevelDocument level = AdoFaiLoader.Load(path).Document;
            LevelAction pause = level.ActionsByFloor[1].Single(action => action.EventType == "Pause");

            PropertyInfo durationProperty = typeof(LevelAction).GetProperty("Duration")
                ?? throw new InvalidOperationException("LevelAction.Duration does not exist yet.");
            if (durationProperty.GetValue(pause) is not double duration || Math.Abs(duration - 2.0) > 1e-9)
                throw new InvalidOperationException($"Pause duration parsing: expected 2, actual {durationProperty.GetValue(pause) ?? "null"}.");

            TimingMap timing = TimingMapBuilder.Build(level);
            LevelDocument baselineLevel = AdoFaiLoader.Load(baselinePath).Document;
            TimingMap baselineTiming = TimingMapBuilder.Build(baselineLevel);

            // Pause duration is measured in beats. At 120 BPM, duration=2 adds
            // exactly 1.0 chart second. Compare against the identical no-Pause
            // chart so the regression is independent of stock's PI precision.
            Near(
                baselineTiming.GetEntryTime(2) + 1.0,
                timing.GetEntryTime(2),
                "Pause timing delta at floor 2");

            FloorTiming pauseFloor = timing.Floors[1];
            Near(1.0, pauseFloor.PauseSeconds, "Pause seconds");

            // During the pause portion the planets must remain at the floor-entry pose.
            PlaybackPose atEntry = timing.GetPose(level, pauseFloor.EntryTime);
            PlaybackPose duringPause = timing.GetPose(level, pauseFloor.EntryTime + 0.5);
            Near(0.0, atEntry.Progress, "Pause entry progress");
            Near(0.0, duringPause.Progress, "Pause hold progress");

            // Once the pause is over, rotation resumes normally. Sample the
            // actual rotation midpoint instead of assuming a mathematically exact
            // 0.5-second tile, because TimingMap intentionally uses stock PI.
            double rotationDuration = pauseFloor.ExitTime - pauseFloor.EntryTime - pauseFloor.PauseSeconds;
            PlaybackPose halfwayThroughRotation = timing.GetPose(
                level,
                pauseFloor.EntryTime + pauseFloor.PauseSeconds + rotationDuration * 0.5);
            Near(0.5, halfwayThroughRotation.Progress, "Pause post-hold rotation progress");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(baselinePath))
                File.Delete(baselinePath);
        }
    }

    private static void Near(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{label}: expected {expected:R}, actual {actual:R}.");
    }
}
