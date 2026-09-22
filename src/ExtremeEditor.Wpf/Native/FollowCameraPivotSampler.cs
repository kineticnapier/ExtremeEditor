using System.Numerics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class FollowCameraPivotSampler
{
    /// <summary>
    /// Replays ADOFAI's normal 2-beat follow-camera transitions up to chartTime.
    /// This is used only to express Player-relative MoveCamera starts in the same
    /// coordinate frame used by the native renderer.
    /// </summary>
    internal static Vector2 Evaluate(LevelDocument level, TimingMap timingMap, double chartTime)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        if (level.Positions.Length == 0 || timingMap.Floors.Count == 0)
            return Vector2.Zero;

        int lastFloor = Math.Min(level.Positions.Length, timingMap.Floors.Count) - 1;
        Vector2 current = level.Positions[0];
        Vector2 from = current;
        Vector2 to = current;
        double startTime = timingMap.Floors[0].EntryTime;
        double duration = 0.0;

        void EvaluateAt(double time)
        {
            double progress = duration <= 1e-9
                ? 1.0
                : Math.Clamp((time - startTime) / duration, 0.0, 1.0);
            current = Vector2.Lerp(from, to, (float)progress);
        }

        for (int floor = 1; floor <= lastFloor; floor++)
        {
            FloorTiming timing = timingMap.Floors[floor];
            if (timing.EntryTime > chartTime + 1e-9)
                break;

            EvaluateAt(timing.EntryTime);
            from = current;
            to = level.Positions[floor];
            startTime = timing.EntryTime;

            double bpm = timing.Bpm > 0.0 ? timing.Bpm : Math.Max(level.InitialBpm, 0.000001);
            duration = 120.0 / bpm;
        }

        EvaluateAt(chartTime);
        return current;
    }
}
