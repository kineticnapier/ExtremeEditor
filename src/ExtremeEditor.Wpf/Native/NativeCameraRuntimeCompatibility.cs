using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    /// <summary>
    /// Compatibility hook retained for the existing native snapshot pipeline.
    /// FaithfulNativeCameraTimelineBuilder now emits the final camera coordinates:
    /// Player starts are already world-space and Tile targets already capture the
    /// transformed floor position at StartEffect time. Do not rebase them here.
    /// </summary>
    internal static void MakePlayerStartsRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(events);
    }
}
