using System.Numerics;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const float SceneCoverageMarginScreens = 1.5f;
    private const float DenseSceneCoverageMarginScreens = 0.5f;
    private const int DenseRasterCandidateThreshold = 2_000;

    private readonly ContainerVisual _sceneRoot = new();
    private readonly DrawingVisual _sceneVisual = new();
    private readonly TranslateTransform _sceneTranslate = new();

    private Vector2 _sceneAnchorCamera;
    private WorldRect _sceneCoverage;
    private float _sceneCacheZoom;
    private double _sceneCacheWidth;
    private double _sceneCacheHeight;
    private bool _sceneCacheReady;
    private int _sceneCandidateCount;

    public int StaticSceneBuildCount { get; private set; }
    public int StaticChunkBuildCount { get; private set; }
    public int StaticSceneLayerCount => _sceneRoot.Children.Count;
    public bool StaticSceneRasterCacheActive { get; private set; }
    public int StaticSceneRasterCacheBuildCount { get; private set; }
    public bool StaticSceneIconsEnabled { get; private set; }

    private void ResetStaticScene()
    {
        EnsureStaticSceneVisualAttached();

        using (DrawingContext drawingContext = _sceneVisual.RenderOpen())
        {
        }

        _sceneCacheReady = false;
        _sceneCandidateCount = 0;
        StaticSceneRasterCacheActive = false;
        StaticSceneIconsEnabled = false;
        _sceneTranslate.X = 0.0;
        _sceneTranslate.Y = 0.0;
        _sceneRoot.Transform = _sceneTranslate;
        ResetRasterChunks();
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

        if (cacheInvalid || (!StaticSceneRasterCacheActive && !Contains(_sceneCoverage, viewport)))
        {
            BuildStaticScene(viewport);
        }
        else
        {
            LastCandidateCount = _sceneCandidateCount;
            UpdateStaticSceneTransform();
            if (StaticSceneRasterCacheActive)
                UpdateRasterChunksForViewport(playbackActive: PlaybackPose is not null);
        }
    }

    private void BuildStaticScene(WorldRect viewport)
    {
        if (_level is null || _index is null)
            return;

        EnsureStaticSceneVisualAttached();

        bool meshPreview = _useFloorPreview && _zoom >= MinMeshPreviewZoom;
        _index.Query(viewport, _candidates);
        bool denseMeshPreview = meshPreview && _candidates.Count > DenseRasterCandidateThreshold;
        float marginScreens = denseMeshPreview
            ? DenseSceneCoverageMarginScreens
            : SceneCoverageMarginScreens;

        float marginX = Math.Max(2f, viewport.Width * marginScreens);
        float marginY = Math.Max(2f, viewport.Height * marginScreens);
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
        _sceneCandidateCount = _candidates.Count;
        LastCandidateCount = _sceneCandidateCount;
        StaticSceneRasterCacheActive = meshPreview && _sceneCandidateCount > DenseRasterCandidateThreshold;
        StaticSceneIconsEnabled = meshPreview && _zoom >= MinIconZoom;

        _renderCameraOverride = _sceneAnchorCamera;
        try
        {
            using DrawingContext drawingContext = _sceneVisual.RenderOpen();

            if (StaticSceneRasterCacheActive)
            {
                // Dense floor rasterization is asynchronous. The UI thread only
                // composes completed frozen bitmaps and lightweight overlays.
                if (StaticSceneIconsEnabled)
                    DrawMeshPreviewIcons(drawingContext, _sceneCoverage);
            }
            else if (meshPreview)
            {
                DrawMeshPreview(drawingContext, _sceneCoverage);
            }
            else
            {
                StaticSceneIconsEnabled = false;
                DrawOverview(drawingContext, _sceneCoverage);
            }
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

        if (StaticSceneRasterCacheActive)
            UpdateRasterChunksForViewport(playbackActive: PlaybackPose is not null);
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
