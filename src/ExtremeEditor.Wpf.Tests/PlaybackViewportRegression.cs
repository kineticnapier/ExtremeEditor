using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackViewportRegression
{
    public static void Run()
    {
        VerifyPlaybackStateAndFollow();
        VerifyPlaybackPoseAddsPlanetDrawings();
        VerifyPlaybackPresenterUpdatesAndClearsPose();
    }

    private static void VerifyPlaybackStateAndFollow()
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

    private static void VerifyPlaybackPoseAddsPlanetDrawings()
    {
        var viewport = new LevelViewport
        {
            UseFloorPreview = false,
            FollowPlayer = false
        };
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        viewport.Measure(new Size(400, 300));
        viewport.Arrange(new Rect(0, 0, 400, 300));
        viewport.FrameAll();

        int withoutPlayback = RenderDrawingNodeCount(viewport);

        Vector2 stationary = level.Positions[2];
        viewport.SetPlaybackPose(new PlaybackPose(
            2,
            0.5,
            stationary,
            stationary + new Vector2(1f, 0f),
            true,
            false));
        int withPlayback = RenderDrawingNodeCount(viewport);

        if (withPlayback < withoutPlayback + 2)
        {
            throw new InvalidOperationException(
                $"Playback pose must add two planet drawings. before={withoutPlayback}, after={withPlayback}.");
        }
    }

    private static void VerifyPlaybackPresenterUpdatesAndClearsPose()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        TimingMap timingMap = TimingMapBuilder.Build(level);
        var viewport = new LevelViewport
        {
            FollowPlayer = false
        };
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        Assembly assembly = typeof(LevelViewport).Assembly;
        Type presenterType = assembly.GetType("ExtremeEditor.Wpf.WpfPlaybackPresenter")
            ?? throw new InvalidOperationException("WpfPlaybackPresenter does not exist yet.");
        MethodInfo update = presenterType.GetMethod(
            "Update",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfPlaybackPresenter.Update is missing.");

        const double audioSeconds = 0.5;
        update.Invoke(null, [viewport, level, timingMap, audioSeconds, false]);

        double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
        PlaybackPose expected = timingMap.GetPose(level, chartTime);
        if (viewport.PlaybackPose is not PlaybackPose actual || actual != expected)
            throw new InvalidOperationException("Playback presenter must map audio time to the viewport playback pose.");

        update.Invoke(null, [viewport, level, timingMap, audioSeconds, true]);
        if (viewport.PlaybackPose is not null)
            throw new InvalidOperationException("Playback presenter must clear the viewport pose when transport is stopped.");
    }

    private static int RenderDrawingNodeCount(LevelViewport viewport)
    {
        MethodInfo onRender = typeof(LevelViewport).GetMethod(
            "OnRender",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.OnRender is missing.");

        var visual = new DrawingVisual();
        using (DrawingContext drawingContext = visual.RenderOpen())
            onRender.Invoke(viewport, [drawingContext]);

        return visual.Drawing is Drawing drawing ? CountDrawingNodes(drawing) : 0;
    }

    private static int CountDrawingNodes(Drawing drawing)
    {
        int count = 1;
        if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
                count += CountDrawingNodes(child);
        }

        return count;
    }
}
