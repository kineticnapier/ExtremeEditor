using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    private const double InitialEventCutoff = -1.0e50;

    /// <summary>
    /// FaithfulNativeCameraTimelineBuilder historically baked the player position
    /// into StartX/StartY while leaving TargetX/TargetY in Player-relative space.
    /// That only works while the player pivot is stationary: during a MoveTrack or
    /// normal follow movement the tween interpolates from an old world-space point
    /// to a moving relative target and the camera drifts away from the player.
    ///
    /// Convert non-initial Player-relative starts back to local follow-space. The
    /// native evaluator then adds the current smooth-follow pivot to both ends on
    /// every frame, matching ADOFAI's camera rig hierarchy.
    /// </summary>
    internal static void MakePlayerStartsRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(events);

        for (int i = 0; i < events.Length; i++)
        {
            ref NativeCameraEvent item = ref events[i];
            if (item.StartTime < InitialEventCutoff)
                continue;

            uint playerFlags = item.Flags &
                (NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY);
            if (playerFlags == 0u)
                continue;

            var pose = timingMap.GetPose(level, item.StartTime);
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerX) != 0u)
                item.StartX -= pose.StationaryPlanet.X;
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerY) != 0u)
                item.StartY -= pose.StationaryPlanet.Y;
        }
    }
}
