using System.Numerics;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const float SceneCoverageMarginScreens = 1.5f;

    private readonly ContainerVisual _sceneRoot = new();
    private readonly DrawingVisual _sceneVisual = new();
    private readonly TranslateTransform _sceneTranslate = new();

    private Vector2 _sceneAnchorCamera;
    private WorldRect _sceneCoverage;
    private float _sceneCacheZoom;
    private double _sceneCacheWidth;
    private double _sceneCacheHeight;
    private bool _sceneCacheReady;

    public int StaticSceneBuildCount { get; private set; }
    public int StaticChunkBuildCount { get; private set; }
    public int StaticSceneLayerCount => _sceneRoot.Children.Count;

    private void ResetStaticScene()
    {
        EnsureStaticSceneVisualAttached();

        using (DrawingContext drawingContext = _sceneVisual.RenderOpen())
        {
        }

        _sceneCacheReady = false;
        _sceneTranslate.X = 0.0;
        _sceneTranslate.Y = 0.0;
        _sceneRoot.Transform = _sceneTranslate;
    }

    private void EnsureSceneCoverage()
    {
        if (_level is null || _index is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        WorldRect viewport = GetViewportWorldRect();
        bool cacheInvalid = !_sceneCacheReady ||
                            MathF.Abs(_sceneCacheZoom - _zoom) > 0.0001f ||
                            Math.Abs(_sceneCacheWidth - ActualWidth) > 0.5 ||
                            Math.Abs(_sceneCacheHeight - ActualHeight) > 0.5;

        if (cacheInvalid || !Contains(_sceneCoverage, viewport))
            BuildStaticScene(viewport);
        else
            UpdateStaticSceneTransform();
    }

    private void BuildStaticScene(WorldRect viewport)
    {
        if (_level is null || _index is null)
            return;

        EnsureStaticSceneVisualAttached();

        float marginX = Math.Max(2f, viewport.Width * SceneCoverageMarginScreens);
        float marginY = Math.Max(2f, viewport.Height * SceneCoverageMarginScreens);
        _sceneCoverage = new WorldRect(
            viewport.Left - marginX,
            viewport.Top - marginY,
            viewport.Right + marginX,
            viewport.Bottom + marginY);

        _sceneAnchorCamera = _camera;
        _sceneCacheZoom = _zoom;
        _sceneCacheWidth = ActualWidth;
        _sceneCacheHeight = ActualHeight;

        _index.Query(_sceneCoverage, _candidates);

        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            using DrawingContext drawingContext = _sceneVisual.RenderOpen();

            // One DrawingVisual owns the complete buffered scene so floor overlap
            // order is exactly the same as the original renderer: candidates are
            // sorted globally by floor index inside DrawMeshPreview/DrawOverview.
            bool meshPreview = _useFloorPreview && _zoom >= MinMeshPreviewZoom;
            if (meshPreview)
                DrawMeshPreview(drawingContext, _sceneCoverage);
            else
                DrawOverview(drawingContext, _sceneCoverage);
        }
        finally
        {
            _renderCameraOverride = null;
        }

        _sceneTranslate.X = 0.0;
        _sceneTranslate.Y = 0.0;
        _sceneRoot.Transform = _sceneTranslate;
        _sceneCacheReady = true;
        StaticSceneBuildCount++;
        StaticChunkBuildCount++;
    }

    private void UpdateStaticSceneTransform()
    {
        if (!_sceneCacheReady)
            return;

        _sceneTranslate.X = (_sceneAnchorCamera.X - _camera.X) * _zoom;
        _sceneTranslate.Y = (_camera.Y - _sceneAnchorCamera.Y) * _zoom;
    }

    private void EnsureStaticSceneVisualAttached()
    {
        if (_sceneRoot.Children.Contains(_sceneVisual))
            return;

        _sceneRoot.Children.Clear();
        _sceneRoot.Children.Add(_sceneVisual);
    }

    private static bool Contains(WorldRect outer, WorldRect inner) =>
        inner.Left >= outer.Left &&
        inner.Top >= outer.Top &&
        inner.Right <= outer.Right &&
        inner.Bottom <= outer.Bottom;
}
