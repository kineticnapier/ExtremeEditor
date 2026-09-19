using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private static readonly Brush PlaybackRedBrush = CreateBrush(235, 72, 72);
    private static readonly Brush PlaybackBlueBrush = CreateBrush(72, 142, 235);
    private static readonly Pen PlaybackPlanetOutlinePen = CreatePlaybackPlanetOutlinePen();

    public bool FollowPlayer { get; set; }
    public PlaybackPose? PlaybackPose { get; private set; }

    public void SetPlaybackPose(PlaybackPose? pose)
    {
        PlaybackPose = pose;
        if (FollowPlayer && pose is PlaybackPose current)
            _camera = current.StationaryPlanet;

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
