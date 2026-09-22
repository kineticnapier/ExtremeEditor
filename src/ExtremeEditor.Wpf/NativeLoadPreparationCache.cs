using System.Runtime.CompilerServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed class NativeLoadPreparationEntry
{
    internal required Task<PreparedNativeLevel> Level { get; init; }
    internal required Task<PreparedNativePlayback> Playback { get; init; }
}

internal static class NativeLoadPreparationCache
{
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<LevelDocument, NativeLoadPreparationEntry> ByLevel = new();
    private static readonly ConditionalWeakTable<TimingMap, NativeLoadPreparationEntry> ByTimingMap = new();

    internal static void Start(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        // Both builders are pure CPU work over the immutable loaded document.
        // Start them before the UI thread enters synchronous AudioPlayer.Load so
        // native preparation overlaps the expensive hit-sound pre-render.
        Task<PreparedNativeLevel> levelTask = Task.Run(
            () => NativeLevelViewportProfilingExtensions.PrepareLevelProfiled(level));
        Task<PreparedNativePlayback> playbackTask = Task.Run(
            () => NativeLevelViewportProfilingExtensions.PreparePlaybackTimelineProfiled(level, timingMap));
        var entry = new NativeLoadPreparationEntry
        {
            Level = levelTask,
            Playback = playbackTask
        };

        lock (Gate)
        {
            ByLevel.Remove(level);
            ByTimingMap.Remove(timingMap);
            ByLevel.Add(level, entry);
            ByTimingMap.Add(timingMap, entry);
        }
    }

    internal static bool TryGetLevel(
        LevelDocument level,
        out Task<PreparedNativeLevel>? preparation)
    {
        lock (Gate)
        {
            if (ByLevel.TryGetValue(level, out NativeLoadPreparationEntry? entry))
            {
                preparation = entry.Level;
                return true;
            }
        }

        preparation = null;
        return false;
    }

    internal static bool TryGetPlayback(
        TimingMap timingMap,
        out Task<PreparedNativePlayback>? preparation)
    {
        lock (Gate)
        {
            if (ByTimingMap.TryGetValue(timingMap, out NativeLoadPreparationEntry? entry))
            {
                preparation = entry.Playback;
                return true;
            }
        }

        preparation = null;
        return false;
    }
}
