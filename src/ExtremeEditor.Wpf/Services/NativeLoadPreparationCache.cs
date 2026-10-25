using System.Runtime.CompilerServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal static class NativeLoadPreparationCache
{
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<LevelDocument, Task<PreparedNativeLevel>> ByLevel = new();
    private static readonly ConditionalWeakTable<TimingMap, Task<PreparedNativePlayback>> ByTimingMap = new();
    private static readonly ConditionalWeakTable<LevelDocument, HashSet<TimingMap>> PlaybackMapsByLevel = new();

    internal static Task<PreparedNativeLevel> StartLevel(LevelDocument level)
        => StartLevel(level, NativeLevelViewportProfilingExtensions.PrepareLevelProfiled);

    internal static Task<PreparedNativeLevel> StartLevel(
        LevelDocument level,
        Func<LevelDocument, PreparedNativeLevel> prepare)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(prepare);

        lock (Gate)
        {
            if (ByLevel.TryGetValue(level, out Task<PreparedNativeLevel>? existing))
            {
                if (!existing.IsFaulted && !existing.IsCanceled)
                    return existing;
                ByLevel.Remove(level);
            }

            Task<PreparedNativeLevel> preparation = Task.Run(() => prepare(level));
            ByLevel.Add(level, preparation);
            _ = preparation.ContinueWith(
                _ => RemoveFailedLevel(level, preparation),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return preparation;
        }
    }

    internal static Task<PreparedNativePlayback> StartPlayback(
        LevelDocument level,
        TimingMap timingMap)
        => StartPlayback(
            level,
            timingMap,
            NativeLevelViewportProfilingExtensions.PreparePlaybackTimelineProfiled);

    internal static Task<PreparedNativePlayback> StartPlayback(
        LevelDocument level,
        TimingMap timingMap,
        Func<LevelDocument, TimingMap, PreparedNativePlayback> prepare)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(prepare);

        lock (Gate)
        {
            if (ByTimingMap.TryGetValue(timingMap, out Task<PreparedNativePlayback>? existing))
            {
                if (!existing.IsFaulted && !existing.IsCanceled)
                    return existing;
                ByTimingMap.Remove(timingMap);
            }

            Task<PreparedNativePlayback> preparation = Task.Run(() => prepare(level, timingMap));
            ByTimingMap.Add(timingMap, preparation);
            PlaybackMapsByLevel.GetOrCreateValue(level).Add(timingMap);
            _ = preparation.ContinueWith(
                _ => RemoveFailedPlayback(timingMap, preparation),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return preparation;
        }
    }

    // EditorSession mutates LevelDocument in place. A snapshot created before an
    // edit must never be restored when that tab is activated again. A running
    // preparation may still finish, but after removal it cannot be retrieved.
    internal static void InvalidateLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        lock (Gate)
        {
            ByLevel.Remove(level);
            if (PlaybackMapsByLevel.TryGetValue(level, out HashSet<TimingMap>? timingMaps))
            {
                foreach (TimingMap timingMap in timingMaps)
                    ByTimingMap.Remove(timingMap);
                PlaybackMapsByLevel.Remove(level);
            }
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

    private static void RemoveFailedLevel(
        LevelDocument level,
        Task<PreparedNativeLevel> failed)
    {
        lock (Gate)
        {
            if (ByLevel.TryGetValue(level, out Task<PreparedNativeLevel>? current) &&
                ReferenceEquals(current, failed))
                ByLevel.Remove(level);
        }
    }

    private static void RemoveFailedPlayback(
        TimingMap timingMap,
        Task<PreparedNativePlayback> failed)
    {
        lock (Gate)
        {
            if (ByTimingMap.TryGetValue(timingMap, out Task<PreparedNativePlayback>? current) &&
                ReferenceEquals(current, failed))
                ByTimingMap.Remove(timingMap);
        }
    }
}
