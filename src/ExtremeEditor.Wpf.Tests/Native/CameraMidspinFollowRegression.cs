using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraMidspinFollowRegression
{
    public static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.InitialBpm = 60.0;
        level.CountdownTicks = 0;
        level.Angles = [0.0, 999.0, 90.0];
        level.RebuildGeometry();

        TimingMap timing = TimingMapBuilder.Build(level);
        if (!timing.Floors[1].MidSpin)
            throw new InvalidOperationException("RED setup: floor 1 must be a Midspin.");

        double transitionTime = timing.Floors[1].EntryTime;
        if (Math.Abs(transitionTime - timing.Floors[2].EntryTime) > 1e-9)
        {
            throw new InvalidOperationException(
                "RED setup: Midspin and following floor must share EntryTime.");
        }

        const double epsilon = 0.001;
        const double followDuration = 2.0; // 2 beats at 60 BPM.
        Vector2 initial = level.Positions[0];
        Vector2 midspinTarget = level.Positions[1];

        AssertNear(
            FollowCameraPivotSampler.Evaluate(level, timing, transitionTime - epsilon),
            initial,
            "Midspin follow pivot immediately before the equal-time transition");
        AssertNear(
            FollowCameraPivotSampler.Evaluate(level, timing, transitionTime),
            initial,
            "Midspin follow pivot at the equal-time transition");
        AssertNear(
            FollowCameraPivotSampler.Evaluate(level, timing, transitionTime + epsilon),
            Vector2.Lerp(initial, midspinTarget, (float)(epsilon / followDuration)),
            "Midspin follow pivot immediately after the equal-time transition");
        AssertNear(
            FollowCameraPivotSampler.Evaluate(level, timing, transitionTime + 1.0),
            Vector2.Lerp(initial, midspinTarget, 0.5f),
            "Midspin follow pivot during the 2-beat transition");
    }

    private static void AssertNear(Vector2 actual, Vector2 expected, string phase)
    {
        if (Vector2.Distance(actual, expected) <= 0.0001f)
            return;

        throw new InvalidOperationException(
            "Midspin equal-time floors were processed as independent follow-camera transitions. " +
            $"Phase={phase}, Expected=({expected.X},{expected.Y}), " +
            $"Actual=({actual.X},{actual.Y}).");
    }
}
