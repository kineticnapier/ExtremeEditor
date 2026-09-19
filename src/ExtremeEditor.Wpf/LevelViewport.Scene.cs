using System.Numerics;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const int SceneFloorBatchSize = 256;

    private readonly ContainerVisual _sceneRoot = new();
    private readonly Dictionary<int, DrawingVisual> _sceneBatches = new();
    private readonly HashSet<int> _sceneBatchCandidates = [];
    private readonly TranslateTransform _sceneTranslate = new();

    private Vector2 _sceneAnchorCamera;
    private float _sceneCacheZoom;
    private double _sceneCacheWidth;
    private double _sceneCacheHeight;
    private bool _sceneCacheReady;

    public int StaticSceneBuildCount { get; private set; }
    public int StaticChunkBuildCount { get; private set; }
    public int StaticSceneLayerCount => 1;

    private void ResetStaticScene()
    {
        _sceneRoot.Children.Clear();
        _sceneBatches.Clear();
        _sceneBatchCandidates.Clear();

        _sceneAnchorCamera = _camera;
        _sceneCacheZoom = _zoom;
        _sceneCacheWidth = ActualWidth;
        _sceneCacheHeight = ActualHeight;

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

        _index.Query(coverage, _candidates);
        _sceneBatchCandidates.Clear();
        foreach (int floor in _candidates)
        {
            if ((uint)floor >= (uint)_level.FloorCount)
                continue;

            _sceneBatchCandidates.Add(floor / SceneFloorBatchSize);
        }

        foreach (int batch in _sceneBatchCandidates)
        {
            if (!_sceneBatches.ContainsKey(batch))
                BuildSceneBatch(batch);
        }
    }

    private void BuildSceneBatch(int batch)
    {
        if (_level is null)
            return;

        int start = checked(batch * SceneFloorBatchSize);
        if (start >= _level.FloorCount)
            return;

        int end = Math.Min(start + SceneFloorBatchSize, _level.FloorCount);
        _candidates.Clear();
        for (int floor = start; floor < end; floor++)
            _candidates.Add(floor);

        var visual = new DrawingVisual();
        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            using DrawingContext drawingContext = visual.RenderOpen();
            WorldRect levelBounds = _level.Bounds.Inflate(2f);

            // At close editing zoom, always preserve the stock tile geometry/material.
            // The retained batches are ordered by floor index so overlap semantics are
            // identical even when adjacent floors were discovered from different areas.
            bool meshPreview = _useFloorPreview && _zoom >= MinMeshPreviewZoom;
            if (meshPreview)
                DrawMeshPreview(drawingContext, levelBounds);
            else
                DrawOverview(drawingContext, levelBounds);
        }
        finally
        {
            _renderCameraOverride = null;
        }

        _sceneBatches.Add(batch, visual);

        // DrawMeshPreview renders each batch from high floor to low floor.
        // Keep the batches themselves in the same global descending floor order:
        // higher-numbered batches first, lower-numbered batches later/on top.
        int insertionIndex = 0;
        foreach (int existingBatch in _sceneBatches.Keys)
        {
            if (existingBatch != batch && existingBatch > batch)
                insertionIndex++;
        }

        _sceneRoot.Children.Insert(insertionIndex, visual);
        StaticChunkBuildCount++;
    }

    private void UpdateStaticSceneTransform()
    {
        if (!_sceneCacheReady)
            return;

        _sceneTranslate.X = (_sceneAnchorCamera.X - _camera.X) * _zoom;
        _sceneTranslate.Y = (_camera.Y - _sceneAnchorCamera.Y) * _zoom;
    }
}
