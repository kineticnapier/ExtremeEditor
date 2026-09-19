using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackRasterPrefetchRegression
{
    public static void Run()
    {
        VerifyAsyncRasterChunkOrchestrationExists();
        VerifyPlaybackFollowDoesNotSynchronouslyBuildDenseRaster();
        VerifyMissingChunkRequestDoesNotBlockPlaybackUpdate();
        VerifyLevelResetAdvancesRasterGeneration();
    }

    private static void VerifyAsyncRasterChunkOrchestrationExists()
    {
        Type type = typeof(LevelViewport);
        string[] requiredMethods =
        [
            "ResetRasterChunks",
            "UpdateRasterChunksForViewport",
            "DrainCompletedRasterChunks",
            "QueueVisibleAndPrefetchChunks",
            "RebuildRasterChunkVisuals"
        ];

        foreach (string name in requiredMethods)
        {
            if (type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is null)
                throw new InvalidOperationException($"LevelViewport.{name} is missing.");
        }
    }

    private static void VerifyPlaybackFollowDoesNotSynchronouslyBuildDenseRaster()
    {
        LevelViewport viewport = CreateDenseViewport(out LevelDocument level);
        viewport.FollowPlayer = true;
        Render(viewport);

        int before = ReadInt(viewport, "PlaybackSynchronousRasterBuildCount");
        for (int i = 0; i < 24; i++)
        {
            Vector2 p = new(i * 1.5f, 0f);
            viewport.SetPlaybackPose(new PlaybackPose(i, 0.5, p, p + Vector2.UnitX, true, false));
        }

        int after = ReadInt(viewport, "PlaybackSynchronousRasterBuildCount");
        if (after != before)
            throw new InvalidOperationException($"Playback performed synchronous raster builds: before={before}, after={after}.");
    }

    private static void VerifyMissingChunkRequestDoesNotBlockPlaybackUpdate()
    {
        LevelViewport viewport = CreateDenseViewport(out _);
        viewport.FollowPlayer = true;
        Render(viewport);

        var watch = Stopwatch.StartNew();
        viewport.SetPlaybackPose(new PlaybackPose(0, 0.5, new Vector2(40f, 0f), new Vector2(41f, 0f), true, false));
        watch.Stop();

        if (ReadInt(viewport, "RasterChunkRequestsQueued") <= 0)
            throw new InvalidOperationException("Playback cache miss must enqueue raster work.");
        if (watch.ElapsedMilliseconds >= 33)
            throw new InvalidOperationException($"Playback update blocked for {watch.ElapsedMilliseconds} ms while raster was unavailable.");
    }

    private static void VerifyLevelResetAdvancesRasterGeneration()
    {
        LevelViewport viewport = CreateDenseViewport(out _);
        long before = ReadLong(viewport, "RasterCacheGeneration");
        LevelDocument replacement = CreateDenseLevel(5_000, 100f);
        viewport.SetLevel(replacement, new SpatialGridIndex(replacement.Positions));
        long after = ReadLong(viewport, "RasterCacheGeneration");
        if (after <= before)
            throw new InvalidOperationException($"Level reset must advance raster generation: before={before}, after={after}.");
    }

    private static LevelViewport CreateDenseViewport(out LevelDocument level)
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        level = CreateDenseLevel(5_000, 0f);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        return viewport;
    }

    private static LevelDocument CreateDenseLevel(int count, float offsetX)
    {
        var positions = new Vector2[count];
        for (int i = 0; i < count; i++)
            positions[i] = new Vector2(offsetX + (i % 100) * 0.08f, (i / 100) * 0.08f);
        return new LevelDocument
        {
            SourcePath = "<playback-raster-prefetch-regression>",
            Angles = new double[Math.Max(0, count - 1)],
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
            InitialBpm = 120,
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

    private static int ReadInt(LevelViewport viewport, string name) =>
        typeof(LevelViewport).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(viewport) is int value
            ? value
            : throw new InvalidOperationException($"LevelViewport.{name} is missing.");

    private static long ReadLong(LevelViewport viewport, string name) =>
        typeof(LevelViewport).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(viewport) is long value
            ? value
            : throw new InvalidOperationException($"LevelViewport.{name} is missing.");

    private static void Render(LevelViewport viewport)
    {
        MethodInfo onRender = typeof(LevelViewport).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.OnRender is missing.");
        var visual = new DrawingVisual();
        using DrawingContext dc = visual.RenderOpen();
        onRender.Invoke(viewport, [dc]);
    }
}
