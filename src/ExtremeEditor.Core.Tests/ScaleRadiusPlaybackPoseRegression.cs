using System.Numerics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class ScaleRadiusPlaybackPoseRegression
{
    public static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60.0;

        // ScaleRadius=200% on destination floor 1 means the first edge/orbit
        // radius is doubled. Geometry already reflects that radius.
        level.Positions =
        [
            Vector2.Zero,
            new Vector2(3.0f, 0.0f),
            new Vector2(4.5f, 0.0f)
        ];
        level.Bounds = PathBuilder.CalculateBounds(level.Positions);

        TimingMap timing = TimingMapBuilder.Build(level);
        FloorTiming first = timing.Floors[0];

        // Do not sample exactly at ExitTime: that timestamp is also the next
        // floor's EntryTime, so FindFloor correctly advances to the next orbit.
        // Validate the radius inside floor 0's interval instead.
        double sampleTime = first.EntryTime + (first.ExitTime - first.EntryTime) * 0.5;
        PlaybackPose pose = timing.GetPose(level, sampleTime);

        float actualRadius = Vector2.Distance(pose.StationaryPlanet, pose.OrbitingPlanet);
        const float expectedRadius = 3.0f;
        if (Math.Abs(actualRadius - expectedRadius) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"ScaleRadius playback orbit radius changed. expected={expectedRadius} actual={actualRadius}");
        }
    }
}
