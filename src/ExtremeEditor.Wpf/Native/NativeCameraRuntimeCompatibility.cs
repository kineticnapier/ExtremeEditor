using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    private const double InitialEventCutoff = -1.0e50;

    /// <summary>
    /// Converts only Player-relative camera starts into the smooth-follow pivot
    /// representation consumed by the native renderer.
    ///
    /// Tile targets are already resolved to world space by
    /// FaithfulNativeCameraTimelineBuilder, matching ffxCameraPlus: floor position
    /// is captured when the MoveCamera effect starts and is not re-read every frame.
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
