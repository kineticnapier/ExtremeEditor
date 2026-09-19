using System.Numerics;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackViewportRegression
{
    public static void Run()
    {
        var viewport = new LevelViewport();
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        Type viewportType = typeof(LevelViewport);
        PropertyInfo followProperty = viewportType.GetProperty("FollowPlayer")
            ?? throw new InvalidOperationException("LevelViewport.FollowPlayer does not exist yet.");
        PropertyInfo poseProperty = viewportType.GetProperty("PlaybackPose")
            ?? throw new InvalidOperationException("LevelViewport.PlaybackPose does not exist yet.");
        MethodInfo setPose = viewportType.GetMethod("SetPlaybackPose", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("LevelViewport.SetPlaybackPose does not exist yet.");

        followProperty.SetValue(viewport, true);
        var pose = new PlaybackPose(
            3,
            0.5,
            new Vector2(12.5f, -7.25f),
            new Vector2(13.5f, -7.25f),
            true,
            false);
        setPose.Invoke(viewport, [pose]);

        if (poseProperty.GetValue(viewport) is not PlaybackPose stored || stored != pose)
            throw new InvalidOperationException("LevelViewport must retain the current playback pose.");

        FieldInfo cameraField = viewportType.GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport camera field is missing.");
        if (cameraField.GetValue(viewport) is not Vector2 camera ||
            Vector2.Distance(camera, pose.StationaryPlanet) > 0.0001f)
        {
            throw new InvalidOperationException("Follow Player must center the camera on the current stationary planet.");
        }

        setPose.Invoke(viewport, [null]);
        if (poseProperty.GetValue(viewport) is not null)
            throw new InvalidOperationException("Clearing playback must remove the viewport playback pose.");
    }
}
