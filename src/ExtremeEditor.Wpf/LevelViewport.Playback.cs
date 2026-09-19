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

    private readonly DrawingVisual _playbackVisual = new();
    private bool _playbackVisualAttached;
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

    // Diagnostic counter for performance regressions. Every full viewport
    // OnRender reaches DrawPlaybackPlanets once; playback-only visual updates do not.
    public int StaticSceneBuildCount { get; private set; }

    protected override int VisualChildrenCount => _playbackVisualAttached ? 1 : 0;

    protected override Visual GetVisualChild(int index)
    {
        if (!_playbackVisualAttached || index != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _playbackVisual;
    }

    public void SetPlaybackPose(PlaybackPose? pose)
    {
        PlaybackPose = pose;

        bool cameraChanged = false;
        if (FollowPlayer && pose is PlaybackPose current && _camera != current.StationaryPlanet)
        {
            _camera = current.StationaryPlanet;
            cameraChanged = true;
        }

        RenderPlaybackVisual();

        // Normal playback only moves the two planet visuals. Re-render the
        // expensive floors/path/icons only when following actually moved camera.
        if (cameraChanged)
            InvalidateVisual();
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
        // This method is deliberately called from LevelViewport.OnRender after
        // the static chart. Keep the playback planets in their own retained
        // DrawingVisual so 60 Hz playback updates do not invalidate that chart.
        StaticSceneBuildCount++;
        RenderPlaybackVisual();
    }

    private void RenderPlaybackVisual()
    {
        EnsurePlaybackVisualAttached();

        using DrawingContext drawingContext = _playbackVisual.RenderOpen();
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

    private void EnsurePlaybackVisualAttached()
    {
        if (_playbackVisualAttached)
            return;

        AddVisualChild(_playbackVisual);
        _playbackVisualAttached = true;
    }

    private static Pen CreatePlaybackPlanetOutlinePen()
    {
        var pen = new Pen(CreateBrush(245, 245, 250, 220), 1.5);
        pen.Freeze();
        return pen;
    }
}
