using System.Numerics;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const float MinSceneChunkWorldSize = 12f;
    private const int MaxCachedSceneChunks = 512;

    private readonly ContainerVisual _sceneRoot = new();
    private readonly Dictionary<long, DrawingVisual> _sceneChunks = new();
    private readonly Queue<long> _sceneChunkOrder = new();
    private readonly TranslateTransform _sceneTranslate = new();

    private Vector2 _sceneAnchorCamera;
    private float _sceneCacheZoom;
    private float _sceneChunkWorldSize = MinSceneChunkWorldSize;
    private double _sceneCacheWidth;
    private double _sceneCacheHeight;
    private bool _sceneCacheReady;

    public int StaticSceneBuildCount { get; private set; }
    public int StaticChunkBuildCount { get; private set; }

    private void ResetStaticScene()
    {
        _sceneRoot.Children.Clear();
        _sceneChunks.Clear();
        _sceneChunkOrder.Clear();

        _sceneAnchorCamera = _camera;
        _sceneCacheZoom = _zoom;
        _sceneCacheWidth = ActualWidth;
        _sceneCacheHeight = ActualHeight;

        double worldWidth = ActualWidth > 0 ? ActualWidth / Math.Max(_zoom, 0.0001f) : 0.0;
        double worldHeight = ActualHeight > 0 ? ActualHeight / Math.Max(_zoom, 0.0001f) : 0.0;
        _sceneChunkWorldSize = Math.Max(
            MinSceneChunkWorldSize,
            (float)(Math.Max(worldWidth, worldHeight) / 8.0));

        _sceneTranslate.X = 0.0;
        _sceneTranslate.Y = 0.0;
        _sceneRoot.Transform = _sceneTranslate;
        _sceneCacheReady = true;
        StaticSceneBuildCount++;
    }

    private void EnsureSceneCoverage()
    {
        if (_level is null || _index is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (!_sceneCacheReady ||
            MathF.Abs(_sceneCacheZoom - _zoom) > 0.0001f ||
            Math.Abs(_sceneCacheWidth - ActualWidth) > 0.5 ||
            Math.Abs(_sceneCacheHeight - ActualHeight) > 0.5)
        {
            ResetStaticScene();
        }

        UpdateStaticSceneTransform();

        WorldRect viewport = GetViewportWorldRect();
        float marginX = Math.Max(2f, viewport.Width * 1.5f);
        float marginY = Math.Max(2f, viewport.Height * 1.5f);
        var coverage = new WorldRect(
            viewport.Left - marginX,
            viewport.Top - marginY,
            viewport.Right + marginX,
            viewport.Bottom + marginY);

        int minX = FastFloor(coverage.Left / _sceneChunkWorldSize);
        int maxX = FastFloor(coverage.Right / _sceneChunkWorldSize);
        int minY = FastFloor(coverage.Top / _sceneChunkWorldSize);
        int maxY = FastFloor(coverage.Bottom / _sceneChunkWorldSize);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                long key = SceneChunkKey(x, y);
                if (_sceneChunks.ContainsKey(key))
                    continue;

                BuildSceneChunk(key, x, y);
            }
        }
    }

    private void BuildSceneChunk(long key, int chunkX, int chunkY)
    {
        if (_level is null || _index is null)
            return;

        float left = chunkX * _sceneChunkWorldSize;
        float top = chunkY * _sceneChunkWorldSize;
        var bounds = new WorldRect(
            left,
            top,
            left + _sceneChunkWorldSize,
            top + _sceneChunkWorldSize);

        _index.Query(bounds, _candidates);

        var visual = new DrawingVisual();
        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            using DrawingContext drawingContext = visual.RenderOpen();

            // At close editing zoom, always preserve the stock tile geometry/material.
            // Density may suppress icons later, but never downgrades nearby floors to dots.
            bool meshPreview = _useFloorPreview && _zoom >= MinMeshPreviewZoom;
            if (meshPreview)
                DrawMeshPreview(drawingContext, bounds);
            else
                DrawOverview(drawingContext, bounds);
        }
        finally
        {
            _renderCameraOverride = null;
        }

        _sceneChunks.Add(key, visual);
        _sceneChunkOrder.Enqueue(key);
        _sceneRoot.Children.Add(visual);
        StaticChunkBuildCount++;

        while (_sceneChunks.Count > MaxCachedSceneChunks && _sceneChunkOrder.Count > 0)
        {
            long oldest = _sceneChunkOrder.Dequeue();
            if (!_sceneChunks.Remove(oldest, out DrawingVisual? oldVisual))
                continue;

            _sceneRoot.Children.Remove(oldVisual);
        }
    }

    private void UpdateStaticSceneTransform()
    {
        if (!_sceneCacheReady)
            return;

        _sceneTranslate.X = (_sceneAnchorCamera.X - _camera.X) * _zoom;
        _sceneTranslate.Y = (_camera.Y - _sceneAnchorCamera.Y) * _zoom;
    }

    private static int FastFloor(float value)
    {
        int i = (int)value;
        return value < i ? i - 1 : i;
    }

    private static long SceneChunkKey(int x, int y) => ((long)x << 32) ^ (uint)y;
}
