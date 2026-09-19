using System.Numerics;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const int RasterChunkPixelSize = 512;

    private readonly RasterChunkCache _rasterChunks = new();
    private readonly RasterChunkWorker _rasterWorker = new();
    private long _rasterGeneration;
    private int _rasterChunkRequestsQueued;
    private int _rasterChunkBuildsCompleted;
    private int _rasterVisibleMissingChunkCount;

    public int PlaybackSynchronousRasterBuildCount { get; private set; }
    public int RasterChunkRequestsQueued => _rasterChunkRequestsQueued;
    public int RasterChunkBuildsCompleted => _rasterChunkBuildsCompleted;
    public int RasterVisibleMissingChunkCount => _rasterVisibleMissingChunkCount;
    public long RasterCacheGeneration => _rasterGeneration;

    private void ResetRasterChunks()
    {
        _rasterGeneration++;
        _rasterChunks.Reset(_rasterGeneration);
        _rasterVisibleMissingChunkCount = 0;
    }

    private void UpdateRasterChunksForViewport(bool playbackActive)
    {
        if (_level is null || _index is null || !StaticSceneRasterCacheActive || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        DrainCompletedRasterChunks();
        QueueVisibleAndPrefetchChunks(GetViewportWorldRect(), playbackActive);
    }

    private void DrainCompletedRasterChunks()
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

        if (changed)
            RebuildRasterChunkVisuals();
    }

    private void QueueVisibleAndPrefetchChunks(WorldRect viewport, bool playbackActive)
    {
        if (_level is null || _index is null)
            return;

        float chunkWorldSize = RasterChunkPixelSize / Math.Max(_zoom, 0.0001f);
        int zoomBucket = checked((int)MathF.Round(_zoom * 1000f));

        float marginX = viewport.Width;
        float marginY = viewport.Height;
        WorldRect requested = new(
            viewport.Left - marginX,
            viewport.Top - marginY,
            viewport.Right + marginX,
            viewport.Bottom + marginY);

        int minX = (int)MathF.Floor(requested.Left / chunkWorldSize);
        int maxX = (int)MathF.Floor(requested.Right / chunkWorldSize);
        int minY = (int)MathF.Floor(requested.Top / chunkWorldSize);
        int maxY = (int)MathF.Floor(requested.Bottom / chunkWorldSize);

        int visibleMissing = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var key = new RasterChunkKey(x, y, zoomBucket, _rasterGeneration);
                if (_rasterChunks.TryGetReady(key, out _))
                    continue;

                WorldRect chunkWorld = new(
                    x * chunkWorldSize,
                    y * chunkWorldSize,
                    (x + 1) * chunkWorldSize,
                    (y + 1) * chunkWorldSize);

                bool visible = Intersects(chunkWorld, viewport);
                if (visible)
                    visibleMissing++;

                if (!_rasterChunks.TryMarkQueued(key))
                    continue;

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
                    _level.Angles);

                _rasterWorker.Enqueue(request);
                _rasterChunkRequestsQueued++;
            }
        }

        _rasterVisibleMissingChunkCount = visibleMissing;
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
        Vector2 old = _camera;
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

    private static bool Intersects(WorldRect a, WorldRect b) =>
        a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;
}
