using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
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
        VerifyManualPanDisablesFollow();
        VerifyFollowPlayerChangeNotification();
        VerifyMainWindowFollowWiring();
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

        int withoutPlayback = PlaybackDrawingNodeCount(viewport);

        Vector2 stationary = level.Positions[2];
        viewport.SetPlaybackPose(new PlaybackPose(
            2,
            0.5,
            stationary,
            stationary + new Vector2(1f, 0f),
            true,
            false));
        int withPlayback = PlaybackDrawingNodeCount(viewport);

        if (withPlayback < withoutPlayback + 2)
        {
            throw new InvalidOperationException(
                $"Playback pose must add two planet drawings to the retained playback visual. before={withoutPlayback}, after={withPlayback}.");
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

    private static void VerifyManualPanDisablesFollow()
    {
        var viewport = new LevelViewport
        {
            FollowPlayer = true
        };

        MethodInfo disableFollow = typeof(LevelViewport).GetMethod(
            "DisableFollowForManualPan",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.DisableFollowForManualPan does not exist yet.");

        disableFollow.Invoke(viewport, null);
        if (viewport.FollowPlayer)
            throw new InvalidOperationException("Manual panning must disable Follow Player.");
    }

    private static void VerifyFollowPlayerChangeNotification()
    {
        var viewport = new LevelViewport();
        EventInfo changedEvent = typeof(LevelViewport).GetEvent("FollowPlayerChanged")
            ?? throw new InvalidOperationException("LevelViewport.FollowPlayerChanged does not exist yet.");

        int changes = 0;
        EventHandler handler = (_, _) => changes++;
        changedEvent.AddEventHandler(viewport, handler);
        viewport.FollowPlayer = true;
        viewport.FollowPlayer = false;
        changedEvent.RemoveEventHandler(viewport, handler);

        if (changes != 2)
            throw new InvalidOperationException($"FollowPlayerChanged must fire once per value change. actual={changes}.");
    }

    private static void VerifyMainWindowFollowWiring()
    {
        var window = new MainWindow();
        try
        {
            Type windowType = typeof(MainWindow);
            FieldInfo followField = windowType.GetField(
                "FollowPlayerToggle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow.FollowPlayerToggle does not exist yet.");
            FieldInfo viewportField = windowType.GetField(
                "Viewport",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow.Viewport field is missing.");

            if (followField.GetValue(window) is not ToggleButton toggle)
                throw new InvalidOperationException("FollowPlayerToggle must be a ToggleButton.");
            if (viewportField.GetValue(window) is not LevelViewport viewport)
                throw new InvalidOperationException("MainWindow.Viewport must be a LevelViewport.");
            if (!toggle.IsEnabled)
                throw new InvalidOperationException("Follow Player toggle must be enabled.");

            toggle.IsChecked = false;
            toggle.IsChecked = true;
            if (!viewport.FollowPlayer)
                throw new InvalidOperationException("Follow Player toggle must update the viewport follow state.");

            viewport.FollowPlayer = false;
            if (toggle.IsChecked != false)
                throw new InvalidOperationException("Viewport follow changes must update the Follow Player toggle.");
        }
        finally
        {
            window.Close();
        }
    }

    private static int PlaybackDrawingNodeCount(LevelViewport viewport)
    {
        FieldInfo playbackVisualField = typeof(LevelViewport).GetField(
            "_playbackVisual",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport._playbackVisual is missing.");

        if (playbackVisualField.GetValue(viewport) is not DrawingVisual visual)
            throw new InvalidOperationException("LevelViewport._playbackVisual must be a DrawingVisual.");

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
