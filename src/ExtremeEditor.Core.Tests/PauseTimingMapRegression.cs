using System.Reflection;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class PauseTimingMapRegression
{
    public static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-pause-{Guid.NewGuid():N}.adofai");
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

            LevelDocument level = AdoFaiLoader.Load(path).Document;
            LevelAction pause = level.ActionsByFloor[1].Single(action => action.EventType == "Pause");

            PropertyInfo durationProperty = typeof(LevelAction).GetProperty("Duration")
                ?? throw new InvalidOperationException("LevelAction.Duration does not exist yet.");
            if (durationProperty.GetValue(pause) is not double duration || Math.Abs(duration - 2.0) > 1e-9)
                throw new InvalidOperationException($"Pause duration parsing: expected 2, actual {durationProperty.GetValue(pause) ?? "null"}.");

            TimingMap timing = TimingMapBuilder.Build(level);

            // 120 BPM => one 180-degree beat is 0.5 s. Floor 0 takes 0.5 s.
            // Pause(2) at floor 1 holds for 1.0 s, then floor 1's normal
            // 180-degree rotation takes another 0.5 s. Floor 2 therefore starts at 2.0 s.
            Near(2.0, timing.GetEntryTime(2), "Pause timing at floor 2");

            // During the pause portion the planets must remain at the floor-entry pose.
            PlaybackPose atEntry = timing.GetPose(level, timing.GetEntryTime(1));
            PlaybackPose duringPause = timing.GetPose(level, timing.GetEntryTime(1) + 0.5);
            Near(0.0, atEntry.Progress, "Pause entry progress");
            Near(0.0, duringPause.Progress, "Pause hold progress");

            // Once the pause is over, rotation resumes normally.
            PlaybackPose halfwayThroughRotation = timing.GetPose(level, timing.GetEntryTime(1) + 1.25);
            Near(0.5, halfwayThroughRotation.Progress, "Pause post-hold rotation progress");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void Near(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{label}: expected {expected:R}, actual {actual:R}.");
    }
}
