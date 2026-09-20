using System.Numerics;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const int RasterChunkPixelSize = 512;
    private const float RasterPrefetchViewportCount = 3f;

    private readonly RasterChunkCache _rasterChunks = new();
    private readonly RasterChunkWorker _rasterWorker = new();
    private long _rasterGeneration;
    private int _rasterChunkRequestsQueued;
    private int _rasterChunkBuildsCompleted;
    private int _rasterVisibleMissingChunkCount;
    private int _rasterPrefetchReadyOrQueuedCount;
    private Vector2 _rasterPlaybackMotion;

    public int PlaybackSynchronousRasterBuildCount { get; private set; }
    public int RasterChunkRequestsQueued => _rasterChunkRequestsQueued;
    public int RasterChunkBuildsCompleted => _rasterChunkBuildsCompleted;
    public int RasterVisibleMissingChunkCount => _rasterVisibleMissingChunkCount;
    public int RasterPrefetchReadyOrQueuedCount => _rasterPrefetchReadyOrQueuedCount;
    public int RasterReadyChunkCount => _rasterChunks.ReadyCount;
    public int RasterPendingChunkCount => _rasterChunks.PendingCount;
    public int RasterChunksCanceled => _rasterWorker.CanceledCount;
    public long RasterCacheGeneration => _rasterGeneration;

    private void ResetRasterChunks()
    {
        _rasterGeneration++;
        _rasterChunks.Reset(_rasterGeneration, GetRasterZoomBucket());
        _rasterWorker.SetDesiredKeys(new HashSet<RasterChunkKey>());
        _rasterVisibleMissingChunkCount = 0;
        _rasterPrefetchReadyOrQueuedCount = 0;
        _rasterPlaybackMotion = Vector2.Zero;
    }

    internal void ShutdownRasterWorker()
    {
        _rasterWorker.SetDesiredKeys(new HashSet<RasterChunkKey>());
        _rasterWorker.Dispose();
    }

    private void UpdateRasterChunksForViewport(bool playbackActive)
    {
        if (_level is null || _index is null || !StaticSceneRasterCacheActive || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        bool readyEvicted = QueueVisibleAndPrefetchChunks(GetViewportWorldRect(), playbackActive);
        bool completed = DrainCompletedRasterChunks(rebuildVisuals: false);
        if (readyEvicted || completed)
            RebuildRasterChunkVisuals();
    }

    private bool DrainCompletedRasterChunks(bool rebuildVisuals = true)
    {
        bool changed = false;
        while (_rasterWorker.TryDequeueCompleted(out RasterChunkResult? result))
        {
            if (result is null || !_rasterChunks.TryPublish(result))
                continue;

            _rasterChunkBuildsCompleted++;
            StaticSceneRasterCacheBuildCount++;
            changed = true;
        }

        if (changed && rebuildVisuals)
            RebuildRasterChunkVisuals();

        return changed;
    }

    private bool QueueVisibleAndPrefetchChunks(WorldRect viewport, bool playbackActive)
    {
        if (_level is null || _index is null)
            return false;

        float chunkWorldSize = RasterChunkPixelSize / Math.Max(_zoom, 0.0001f);
        int zoomBucket = GetRasterZoomBucket();

        WorldRect safety = new(
            viewport.Left - viewport.Width,
            viewport.Top - viewport.Height,
            viewport.Right + viewport.Width,
            viewport.Bottom + viewport.Height);

        bool hasForwardMotion = playbackActive && _rasterPlaybackMotion.LengthSquared() > 0.000001f;
        WorldRect forward = viewport;
        if (hasForwardMotion)
        {
            Vector2 direction = Vector2.Normalize(_rasterPlaybackMotion);
            float dx = direction.X * viewport.Width * RasterPrefetchViewportCount;
            float dy = direction.Y * viewport.Height * RasterPrefetchViewportCount;
            forward = new WorldRect(
                Math.Min(viewport.Left, viewport.Left + dx),
                Math.Min(viewport.Top, viewport.Top + dy),
                Math.Max(viewport.Right, viewport.Right + dx),
                Math.Max(viewport.Bottom, viewport.Bottom + dy));
        }

        WorldRect requested = Union(safety, forward);
        int minX = (int)MathF.Floor(requested.Left / chunkWorldSize);
        int maxX = (int)MathF.Floor(requested.Right / chunkWorldSize);
        int minY = (int)MathF.Floor(requested.Top / chunkWorldSize);
        int maxY = (int)MathF.Floor(requested.Bottom / chunkWorldSize);

        var desired = new HashSet<RasterChunkKey>();
        var chunks = new List<(RasterChunkKey Key, WorldRect World, bool Visible, bool Ahead, int Priority)>();

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                WorldRect chunkWorld = new(
                    x * chunkWorldSize,
                    y * chunkWorldSize,
                    (x + 1) * chunkWorldSize,
                    (y + 1) * chunkWorldSize);

                bool visible = Intersects(chunkWorld, viewport);
                bool ahead = !visible && hasForwardMotion && Intersects(chunkWorld, forward);
                int priority = visible ? 0 : ahead ? 1 : 2;
                var key = new RasterChunkKey(x, y, zoomBucket, _rasterGeneration);
                desired.Add(key);
                chunks.Add((key, chunkWorld, visible, ahead, priority));
            }
        }

        int evictedReady = _rasterChunks.EvictReadyOutside(desired);
        _rasterChunks.EvictPendingOutside(desired);
        _rasterWorker.SetDesiredKeys(desired);

        var queuedByPriority = new List<(RasterChunkKey Key, WorldRect World)>[]
        {
            new(),
            new(),
            new()
        };

        int visibleMissing = 0;
        int prefetchReadyOrQueued = 0;

        foreach ((RasterChunkKey key, WorldRect chunkWorld, bool visible, bool ahead, int priority) in chunks)
        {
            if (_rasterChunks.TryGetReady(key, out _))
            {
                if (ahead)
                    prefetchReadyOrQueued++;
                continue;
            }

            if (visible)
                visibleMissing++;

            if (!_rasterChunks.TryMarkQueued(key))
            {
                if (ahead)
                    prefetchReadyOrQueued++;
                continue;
            }

            if (ahead)
                prefetchReadyOrQueued++;
            queuedByPriority[priority].Add((key, chunkWorld));
        }

        for (int priority = 0; priority < queuedByPriority.Length; priority++)
        {
            foreach ((RasterChunkKey key, WorldRect chunkWorld) in queuedByPriority[priority])
            {
                var candidates = new List<int>(1024);
                _index.Query(chunkWorld, candidates);
                candidates.Sort(static (a, b) => b.CompareTo(a));

                Rect screenRect = WorldRectToSceneRect(chunkWorld);
                var request = new RasterChunkRequest(
                    key,
                    new Rect(chunkWorld.Left, chunkWorld.Top, chunkWorld.Width, chunkWorld.Height),
                    screenRect,
                    RasterChunkPixelSize,
                    RasterChunkPixelSize,
                    _zoom,
                    candidates.ToArray(),
                    _level.Positions,
                    _level.Angles,
                    priority);

                _rasterWorker.Enqueue(request);
                _rasterChunkRequestsQueued++;
            }
        }

        _rasterVisibleMissingChunkCount = visibleMissing;
        _rasterPrefetchReadyOrQueuedCount = prefetchReadyOrQueued;
        return evictedReady > 0;
    }

    private void RebuildRasterChunkVisuals()
    {
        if (_level is null)
            return;

        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            using DrawingContext dc = _sceneVisual.RenderOpen();
            foreach (RasterChunkKey key in _rasterChunks.GetReadyKeys())
            {
                if (_rasterChunks.TryGetReady(key, out RasterChunkResult? result) && result is not null)
                    dc.DrawImage(result.Bitmap, result.ScreenRect);
            }

            if (StaticSceneIconsEnabled)
                DrawMeshPreviewIcons(dc, _sceneCoverage);
        }
        finally
        {
            _renderCameraOverride = null;
        }
    }

    private Rect WorldRectToSceneRect(WorldRect world)
    {
        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            Point a = WorldToScreen(new Vector2(world.Left, world.Top));
            Point b = WorldToScreen(new Vector2(world.Right, world.Bottom));
            return new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(b.X - a.X),
                Math.Abs(b.Y - a.Y));
        }
        finally
        {
            _renderCameraOverride = null;
        }
    }

    private int GetRasterZoomBucket() => checked((int)MathF.Round(_zoom * 1000f));

    private static WorldRect Union(WorldRect a, WorldRect b) => new(
        Math.Min(a.Left, b.Left),
        Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Max(a.Bottom, b.Bottom));

    private static bool Intersects(WorldRect a, WorldRect b) =>
        a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;
}
