using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    private const double InitialEventCutoff = -1.0e90;

    /// <summary>
    /// Converts builder Player starts from StartEffect-time world coordinates into
    /// the Player-local coordinates expected by the native camera ABI. Targets are
    /// already Player-local. Rebase against the same 2-beat smooth-follow pivot the
    /// native renderer adds back at runtime, not the raw stationary planet.
    /// The synthetic initial event is already local and must not be rebased.
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

            var pivot = FollowCameraPivotSampler.Evaluate(level, timingMap, item.StartTime);
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerX) != 0u)
                item.StartX -= pivot.X;
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerY) != 0u)
                item.StartY -= pivot.Y;
        }
    }
}
