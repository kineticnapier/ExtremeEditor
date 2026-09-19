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
        VerifySetLevelStartsNearFloorZero();
        VerifyPlaybackPoseReusesStaticScene();
        VerifyFollowCameraReusesStaticScene();
        VerifyRetainedScenePreservesGlobalFloorOrder();
        VerifyDenseStockTileSceneUsesRasterCache();
    }

    private static void VerifySetLevelStartsNearFloorZero()
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = CreateLongLevel();
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        FieldInfo cameraField = typeof(LevelViewport).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport camera field is missing.");
        FieldInfo zoomField = typeof(LevelViewport).GetField(
            "_zoom",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport zoom field is missing.");

        if (cameraField.GetValue(viewport) is not Vector2 camera)
            throw new InvalidOperationException("LevelViewport camera must be Vector2.");
        if (zoomField.GetValue(viewport) is not float zoom)
            throw new InvalidOperationException("LevelViewport zoom must be float.");

        if (Vector2.Distance(camera, level.Positions[0]) > 0.001f)
        {
            throw new InvalidOperationException(
                $"Opening a level must start at floor 0 instead of framing the whole chart. camera={camera} floor0={level.Positions[0]}.");
        }

        if (zoom < 8f)
        {
            throw new InvalidOperationException(
                $"Opening a level must keep a close tile-visible zoom. actual={zoom}.");
        }
    }

    private static void VerifyPlaybackPoseReusesStaticScene()
    {
        var viewport = CreateViewportWithSyntheticLevel(out LevelDocument level);
        PropertyInfo buildCountProperty = GetBuildCountProperty();

        Render(viewport);
        int before = ReadBuildCount(viewport, buildCountProperty);

        viewport.SetPlaybackPose(new PlaybackPose(
            0,
            0.25,
            level.Positions[0],
            level.Positions[0] + new Vector2(1f, 0f),
            true,
            false));
        Render(viewport);

        int after = ReadBuildCount(viewport, buildCountProperty);
        if (after != before)
        {
            throw new InvalidOperationException(
                $"Playback-only redraw rebuilt static scene: before {before}, after {after}.");
        }
    }

    private static void VerifyFollowCameraReusesStaticScene()
    {
        var viewport = CreateViewportWithSyntheticLevel(out LevelDocument level);
        viewport.FollowPlayer = true;
        PropertyInfo buildCountProperty = GetBuildCountProperty();

        Render(viewport);
        int before = ReadBuildCount(viewport, buildCountProperty);

        // Follow normally advances to a nearby floor each frame. Buffered motion
        // must reuse the retained static scene; only exhausting the buffer may rebuild it.
        int targetFloor = Math.Min(1, level.FloorCount - 1);
        Vector2 stationary = level.Positions[targetFloor];
        viewport.SetPlaybackPose(new PlaybackPose(
            targetFloor,
            0.5,
            stationary,
            stationary + new Vector2(1f, 0f),
            true,
            false));
        Render(viewport);

        int after = ReadBuildCount(viewport, buildCountProperty);
        if (after != before)
        {
            throw new InvalidOperationException(
                $"Buffered Follow Player camera motion rebuilt static scene: before {before}, after {after}. Camera motion must use a retained transform.");
        }
    }

    private static void VerifyRetainedScenePreservesGlobalFloorOrder()
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = CreateLongLevel();
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        PropertyInfo layerCount = typeof(LevelViewport).GetProperty(
            "StaticSceneLayerCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.StaticSceneLayerCount does not exist yet. The retained scene must expose one globally ordered static layer.");

        if (layerCount.GetValue(viewport) is not int count || count != 1)
        {
            throw new InvalidOperationException(
                $"Stock floor overlap order requires one globally ordered retained scene. actual layers={layerCount.GetValue(viewport)}.");
        }
    }

    private static void VerifyDenseStockTileSceneUsesRasterCache()
    {
        PropertyInfo activeProperty = typeof(LevelViewport).GetProperty(
            "StaticSceneRasterCacheActive",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.StaticSceneRasterCacheActive does not exist yet. Dense stock-tile scenes must switch to a retained raster cache instead of retaining thousands of vector floor drawings.");
        PropertyInfo buildCountProperty = typeof(LevelViewport).GetProperty(
            "StaticSceneRasterCacheBuildCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.StaticSceneRasterCacheBuildCount does not exist yet.");

        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = CreateDenseLevel(5_000);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        Render(viewport);

        if (activeProperty.GetValue(viewport) is not true)
            throw new InvalidOperationException("A dense close-zoom stock-tile scene must use the raster cache.");
        if (buildCountProperty.GetValue(viewport) is not int builds || builds <= 0)
            throw new InvalidOperationException("Dense stock-tile rendering must build at least one raster cache image.");
    }

    private static LevelViewport CreateViewportWithSyntheticLevel(out LevelDocument level)
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        level = LevelDocument.CreateSynthetic(4096);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        return viewport;
    }

    private static LevelDocument CreateLongLevel()
    {
        Vector2[] positions =
        [
            Vector2.Zero,
            new Vector2(1000f, 0f)
        ];

        return new LevelDocument
        {
            SourcePath = "<performance-test>",
            Angles = [0.0],
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
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
            SourcePath = "<dense-performance-test>",
            Angles = new double[Math.Max(0, floorCount - 1)],
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
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

    private static PropertyInfo GetBuildCountProperty()
    {
        return typeof(LevelViewport).GetProperty(
                   "StaticSceneBuildCount",
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException("LevelViewport.StaticSceneBuildCount does not exist yet.");
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
}
