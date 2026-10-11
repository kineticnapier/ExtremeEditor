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

        PlaybackPose end = timing.GetPose(level, first.ExitTime);

        if (Vector2.Distance(end.OrbitingPlanet, level.Positions[1]) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"ScaleRadius playback orbit must land on the destination floor without teleporting. expected={level.Positions[1]} actual={end.OrbitingPlanet}");
        }
    }
}
