using System.Runtime.CompilerServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal static class NativeLoadPreparationCache
{
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<LevelDocument, Task<PreparedNativeLevel>> ByLevel = new();
    private static readonly ConditionalWeakTable<TimingMap, Task<PreparedNativePlayback>> ByTimingMap = new();

    internal static Task<PreparedNativeLevel> StartLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        lock (Gate)
        {
            if (ByLevel.TryGetValue(level, out Task<PreparedNativeLevel>? existing))
                return existing;

            Task<PreparedNativeLevel> preparation = Task.Run(
                () => NativeLevelViewportProfilingExtensions.PrepareLevelProfiled(level));
            ByLevel.Add(level, preparation);
            return preparation;
        }
    }

    internal static Task<PreparedNativePlayback> StartPlayback(
        LevelDocument level,
        TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        lock (Gate)
        {
            if (ByTimingMap.TryGetValue(timingMap, out Task<PreparedNativePlayback>? existing))
                return existing;

            Task<PreparedNativePlayback> preparation = Task.Run(
                () => NativeLevelViewportProfilingExtensions.PreparePlaybackTimelineProfiled(level, timingMap));
            ByTimingMap.Add(timingMap, preparation);
            return preparation;
        }
    }

    // Compatibility helper for callers that intentionally want both phases at once.
    internal static void Start(LevelDocument level, TimingMap timingMap)
    {
        _ = StartLevel(level);
        _ = StartPlayback(level, timingMap);
    }

    internal static bool TryGetLevel(
        LevelDocument level,
        out Task<PreparedNativeLevel>? preparation)
    {
        lock (Gate)
            return ByLevel.TryGetValue(level, out preparation);
    }

    internal static bool TryGetPlayback(
        TimingMap timingMap,
        out Task<PreparedNativePlayback>? preparation)
    {
        lock (Gate)
            return ByTimingMap.TryGetValue(timingMap, out preparation);
    }
}
