using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PerformanceRegression
{
    public static void Run()
    {
        VerifyPlaybackPoseReusesStaticScene();
    }

    private static void VerifyPlaybackPoseReusesStaticScene()
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = LevelDocument.CreateSynthetic(4096);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        PropertyInfo buildCountProperty = typeof(LevelViewport).GetProperty(
            "StaticSceneBuildCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.StaticSceneBuildCount does not exist yet.");

        Render(viewport);
        int before = ReadBuildCount(viewport, buildCountProperty);

        viewport.SetPlaybackPose(new PlaybackPose(
            0,
            0.25,
            level.Positions[0],
            level.Positions[0] + new Vector2(1f, 0f),
            true,
            false));

        int after = ReadBuildCount(viewport, buildCountProperty);
        if (after != before)
        {
            throw new InvalidOperationException(
                $"Playback-only update rebuilt static scene: before {before}, after {after}.");
        }

        if (viewport.VisualChildrenCountForTest() != 1)
            throw new InvalidOperationException("Playback must render through one retained child visual.");
    }

    private static int ReadBuildCount(LevelViewport viewport, PropertyInfo property)
    {
        return property.GetValue(viewport) is int count
            ? count
            : throw new InvalidOperationException("LevelViewport.StaticSceneBuildCount must be an int.");
    }

    private static void Render(LevelViewport viewport)
    {
        MethodInfo onRender = typeof(LevelViewport).GetMethod(
            "OnRender",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.OnRender is missing.");

        var visual = new DrawingVisual();
        using DrawingContext drawingContext = visual.RenderOpen();
        onRender.Invoke(viewport, [drawingContext]);
    }

    private static int VisualChildrenCountForTest(this LevelViewport viewport)
    {
        PropertyInfo property = typeof(LevelViewport).GetProperty(
            "VisualChildrenCount",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.VisualChildrenCount is missing.");

        return property.GetValue(viewport) is int count
            ? count
            : throw new InvalidOperationException("LevelViewport.VisualChildrenCount must be an int.");
    }
}
