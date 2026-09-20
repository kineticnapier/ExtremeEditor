using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private static readonly Brush PlaybackRedBrush = CreateBrush(235, 72, 72);
    private static readonly Brush PlaybackBlueBrush = CreateBrush(72, 142, 235);
    private static readonly Pen PlaybackPlanetOutlinePen = CreatePlaybackPlanetOutlinePen();

    private readonly DrawingVisual _playbackFloorVisual = new();
    private readonly DrawingVisual _playbackVisual = new();
    private bool _followPlayer;

    public event EventHandler? FollowPlayerChanged;

    public bool FollowPlayer
    {
        get => _followPlayer;
        set
        {
            if (_followPlayer == value)
                return;

            _followPlayer = value;
            FollowPlayerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PlaybackPose? PlaybackPose { get; private set; }

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _sceneRoot,
        1 => _playbackFloorVisual,
        2 => _playbackVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal void SetPlaybackFrame(TimingMap timingMap, double chartTime, PlaybackPose pose)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        PlaybackPose = pose;

        bool useTemporal = _useFloorPreview &&
                           _zoom >= MinMeshPreviewZoom &&
                           (TemporalPlaybackActive || StaticSceneRasterCacheActive);

        if (useTemporal && !TemporalPlaybackActive)
            EnterTemporalPlaybackMode();

        if (FollowPlayer && _camera != pose.StationaryPlanet)
        {
            _camera = pose.StationaryPlanet;
            if (!TemporalPlaybackActive)
            {
                UpdateStaticSceneTransform();
                EnsureSceneCoverage();
            }
        }

        if (TemporalPlaybackActive)
            RenderTemporalPlaybackFloors(timingMap, chartTime);
        else if (StaticSceneRasterCacheActive)
            DrainCompletedRasterChunks();

        RenderPlaybackVisual();
    }

    internal void ClearPlaybackFrame()
    {
        PlaybackPose = null;
        bool wasTemporal = TemporalPlaybackActive;
        TemporalPlaybackActive = false;
        ClearTemporalPlaybackVisual();
        _sceneRoot.Opacity = 1.0;

        if (wasTemporal && _level is not null)
        {
            ResetStaticScene();
            EnsureSceneCoverage();
        }

        RenderPlaybackVisual();
    }

    private void EnterTemporalPlaybackMode()
    {
        TemporalPlaybackActive = true;
        _sceneRoot.Opacity = 0.0;
        ResetRasterChunks();
    }

    // Pose-only compatibility path used by existing callers/tests. Production
    // playback supplies timing context through SetPlaybackFrame instead.
    public void SetPlaybackPose(PlaybackPose? pose)
    {
        PlaybackPose = pose;

        if (FollowPlayer && pose is PlaybackPose current && _camera != current.StationaryPlanet)
        {
            _rasterPlaybackMotion = current.StationaryPlanet - _camera;
            _camera = current.StationaryPlanet;
            UpdateStaticSceneTransform();

            if (StaticSceneRasterCacheActive)
            {
                // Update the desired streaming window before accepting worker completions,
                // so obsolete chunks cannot re-enter the retained scene.
                UpdateRasterChunksForViewport(playbackActive: true);
            }
            else
            {
                EnsureSceneCoverage();
            }
        }
        else if (StaticSceneRasterCacheActive)
        {
            DrainCompletedRasterChunks();
        }

        RenderPlaybackVisual();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
            DisableFollowForManualPan();

        base.OnPreviewMouseDown(e);
    }

    private void DisableFollowForManualPan()
    {
        FollowPlayer = false;
    }

    private void DrawPlaybackPlanets(DrawingContext drawingContext)
    {
        // Selection and playback planets share one lightweight dynamic overlay.
        // Static floor/icon content remains retained beneath it.
        RenderPlaybackVisual();
    }

    private void RenderPlaybackVisual()
    {
        using DrawingContext drawingContext = _playbackVisual.RenderOpen();
        DrawSelectedFloorOverlay(drawingContext);

        if (PlaybackPose is not PlaybackPose pose)
            return;

        double radius = Math.Clamp(_zoom * 0.28, 6.0, 28.0);
        Brush stationaryBrush = pose.StationaryIsRed ? PlaybackRedBrush : PlaybackBlueBrush;
        Brush orbitingBrush = pose.StationaryIsRed ? PlaybackBlueBrush : PlaybackRedBrush;

        drawingContext.DrawEllipse(
            stationaryBrush,
            PlaybackPlanetOutlinePen,
            WorldToScreen(pose.StationaryPlanet),
            radius,
            radius);
        drawingContext.DrawEllipse(
            orbitingBrush,
            PlaybackPlanetOutlinePen,
            WorldToScreen(pose.OrbitingPlanet),
            radius,
            radius);
    }

    private static Pen CreatePlaybackPlanetOutlinePen()
    {
        var pen = new Pen(CreateBrush(245, 245, 250, 220), 1.5);
        pen.Freeze();
        return pen;
    }
}
