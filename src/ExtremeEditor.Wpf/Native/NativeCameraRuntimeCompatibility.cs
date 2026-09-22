using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    private const double InitialEventCutoff = -1.0e90;

    /// <summary>
    /// Converts builder Player starts from StartEffect-time world coordinates into
    /// the Player-local coordinates expected by the native camera ABI. Targets are
    /// already Player-local. The synthetic initial event is already local and must
    /// not be rebased.
    /// </summary>
    internal static void MakePlayerStartsRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(events);

        foreach (ref NativeCameraEvent item in events.AsSpan())
        {
            if (item.StartTime <= InitialEventCutoff)
                continue;

            uint playerFlags = item.Flags &
                (NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY);
            if (playerFlags == 0u)
                continue;

            PlaybackPose pose = timingMap.GetPose(level, item.StartTime);
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerX) != 0u)
                item.StartX -= pose.StationaryPlanet.X;
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerY) != 0u)
                item.StartY -= pose.StationaryPlanet.Y;
        }
    }
}
