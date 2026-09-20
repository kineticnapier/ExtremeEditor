using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class RasterSceneRebaseRegression
{
    public static void Run()
    {
        VerifyDenseFollowRebasesSceneAnchor();
        VerifyRasterIconCoverageTracksCurrentViewport();
    }

    private static void VerifyDenseFollowRebasesSceneAnchor()
    {
        LevelViewport viewport = CreateDenseViewport();
        if (!viewport.StaticSceneRasterCacheActive)
            throw new InvalidOperationException("Raster scene rebase regression requires dense raster mode.");

        FieldInfo anchorField = typeof(LevelViewport).GetField(
            "_sceneAnchorCamera",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport._sceneAnchorCamera is missing.");

        viewport.FollowPlayer = true;
        Vector2 farCamera = new(10_000f, -8_000f);
        viewport.SetPlaybackPose(new PlaybackPose(
            10,
            0.5,
            farCamera,
            farCamera + Vector2.UnitX,
            true,
            false));

        if (anchorField.GetValue(viewport) is not Vector2 anchor)
            throw new InvalidOperationException("LevelViewport._sceneAnchorCamera must be a Vector2.");

        if (Vector2.Distance(anchor, farCamera) > 0.01f)
        {
            throw new InvalidOperationException(
                $"Dense Follow Player did not rebase the scene anchor after a large camera jump. anchor={anchor}, camera={farCamera}.");
        }

        viewport.ShutdownRasterWorker();
    }

    private static void VerifyRasterIconCoverageTracksCurrentViewport()
    {
        LevelViewport viewport = CreateDenseViewport();
        MethodInfo iconCoverageMethod = typeof(LevelViewport).GetMethod(
            "GetRasterIconCoverage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "LevelViewport.GetRasterIconCoverage is missing. Dense raster icons must use the current viewport instead of stale scene coverage.");

        viewport.FollowPlayer = true;
        Vector2 farCamera = new(10_000f, -8_000f);
        viewport.SetPlaybackPose(new PlaybackPose(
            10,
            0.5,
            farCamera,
            farCamera + Vector2.UnitX,
            true,
            false));

        object? coverageValue = iconCoverageMethod.Invoke(viewport, null);
        if (coverageValue is not WorldRect coverage)
            throw new InvalidOperationException("GetRasterIconCoverage must return WorldRect.");

        if (!coverage.Contains(farCamera))
        {
            throw new InvalidOperationException(
                $"Dense raster icon coverage did not follow the current viewport. coverage={coverage}, camera={farCamera}.");
        }

        viewport.ShutdownRasterWorker();
    }

    private static LevelViewport CreateDenseViewport()
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = CreateDenseLevel(5_000);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        Render(viewport);
        return viewport;
    }

    private static LevelDocument CreateDenseLevel(int floorCount)
    {
        var positions = new Vector2[floorCount];
        const int columns = 100;
        const float spacing = 0.08f;
        for (int floor = 0; floor < floorCount; floor++)
        {
            positions[floor] = new Vector2(
                (floor % columns) * spacing,
                (floor / columns) * spacing);
        }

        return new LevelDocument
        {
            SourcePath = "<raster-scene-rebase-regression>",
            Angles = new double[Math.Max(0, floorCount - 1)],
            Positions = positions,
            ActionCount = 1,
            ActionTypeCounts = new Dictionary<string, int> { ["Twirl"] = 1 },
            ActionsByFloor = new Dictionary<int, LevelAction[]>
            {
                [0] = [new LevelAction(0, "Twirl", true, null, null, null, null)]
            },
            InitialBpm = 120.0,
            SongFilename = null,
            OffsetMilliseconds = 0,
            PitchPercent = 100,
            CountdownTicks = 0,
            SeparateCountdownTime = false,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = PathBuilder.CalculateBounds(positions)
        };
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
}
