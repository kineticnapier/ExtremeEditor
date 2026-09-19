using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    public bool FollowPlayer { get; set; }
    public PlaybackPose? PlaybackPose { get; private set; }

    public void SetPlaybackPose(PlaybackPose? pose)
    {
        PlaybackPose = pose;
        if (FollowPlayer && pose is PlaybackPose current)
            _camera = current.StationaryPlanet;

        InvalidateVisual();
    }
}
