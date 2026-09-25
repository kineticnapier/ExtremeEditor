using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class TemporalPlaybackRoutingRegression
{
    public static void Run()
    {
        VerifyDensePlaybackUsesTemporalRoutingWithoutRasterGrowth();
        VerifyStoppingExitsTemporalMode();
        VerifyNonDensePlaybackKeepsStaticPath();
    }

    private static void VerifyDensePlaybackUsesTemporalRoutingWithoutRasterGrowth()
    {
        LevelViewport viewport = CreateDenseViewport(out LevelDocument level);
        try
        {
            if (!viewport.StaticSceneRasterCacheActive)
                throw new InvalidOperationException("Temporal routing regression requires dense raster mode before playback starts.");

            MethodInfo setFrame = GetRequiredMethod("SetPlaybackFrame");
            TimingMap timingMap = TimingMapBuilder.Build(level);
            viewport.FollowPlayer = true;

            int firstFloor = 100;
            double firstTime = timingMap.GetEntryTime(firstFloor);
            setFrame.Invoke(viewport, [timingMap, firstTime, timingMap.GetPose(level, firstTime)]);

            if (!viewport.TemporalPlaybackActive)
                throw new InvalidOperationException("Dense active playback must enter temporal rendering mode.");

            int requestsAfterEntry = viewport.RasterChunkRequestsQueued;
            int[] floors = [600, 1_100, 1_600, 2_100, 2_600];
            foreach (int floor in floors)
            {
                double chartTime = timingMap.GetEntryTime(floor);
                setFrame.Invoke(viewport, [timingMap, chartTime, timingMap.GetPose(level, chartTime)]);
            }

            if (viewport.RasterChunkRequestsQueued != requestsAfterEntry)
            {
                throw new InvalidOperationException(
                    $"Temporal dense playback must not queue raster chunks while Follow Player advances. " +
                    $"before={requestsAfterEntry}, after={viewport.RasterChunkRequestsQueued}.");
            }

            int beforeResize = viewport.RasterChunkRequestsQueued;
            viewport.Measure(new Size(900, 650));
            viewport.Arrange(new Rect(0, 0, 900, 650));
            Render(viewport);
            if (viewport.RasterChunkRequestsQueued != beforeResize)
            {
                throw new InvalidOperationException(
                    $"Resizing during temporal playback must not wake raster maintenance. " +
                    $"before={beforeResize}, after={viewport.RasterChunkRequestsQueued}.");
            }
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }

    private static void VerifyStoppingExitsTemporalMode()
    {
        LevelViewport viewport = CreateDenseViewport(out LevelDocument level);
        try
        {
            MethodInfo setFrame = GetRequiredMethod("SetPlaybackFrame");
            MethodInfo clearFrame = GetRequiredMethod("ClearPlaybackFrame");
            TimingMap timingMap = TimingMapBuilder.Build(level);
            double chartTime = timingMap.GetEntryTime(100);

            setFrame.Invoke(viewport, [timingMap, chartTime, timingMap.GetPose(level, chartTime)]);
            if (!viewport.TemporalPlaybackActive)
                throw new InvalidOperationException("Dense playback must be temporal before stop lifecycle is tested.");

            clearFrame.Invoke(viewport, null);
            if (viewport.TemporalPlaybackActive)
                throw new InvalidOperationException("Stopping playback must exit temporal rendering mode.");
            if (viewport.PlaybackPose is not null)
                throw new InvalidOperationException("Stopping playback must clear the retained playback pose.");
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }

    private static void VerifyNonDensePlaybackKeepsStaticPath()
    {
        var viewport = new LevelViewport();
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        Render(viewport);

        try
        {
            MethodInfo setFrame = GetRequiredMethod("SetPlaybackFrame");
            TimingMap timingMap = TimingMapBuilder.Build(level);
            double chartTime = timingMap.GetEntryTime(Math.Min(2, level.FloorCount - 1));
            setFrame.Invoke(viewport, [timingMap, chartTime, timingMap.GetPose(level, chartTime)]);

            if (viewport.TemporalPlaybackActive)
                throw new InvalidOperationException("Non-dense playback must keep the existing static scene path.");
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }

    private static MethodInfo GetRequiredMethod(string name) =>
        typeof(LevelViewport).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"LevelViewport.{name} is missing.");

    private static LevelViewport CreateDenseViewport(out LevelDocument level)
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        level = CreateDenseLevel(5_000);
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

        positions[600] = new Vector2(10_000f, -8_000f);
        positions[1_100] = new Vector2(-12_000f, 9_000f);
        positions[1_600] = new Vector2(16_000f, 12_000f);
        positions[2_100] = new Vector2(-18_000f, -14_000f);
        positions[2_600] = new Vector2(22_000f, -16_000f);

        return new LevelDocument
        {
            SourcePath = "<temporal-playback-routing-regression>",
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
