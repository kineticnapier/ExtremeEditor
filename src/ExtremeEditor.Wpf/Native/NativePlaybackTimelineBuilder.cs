using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativePlaybackTimelineBuilder
{
    public static NativePlaybackTiming[] Build(TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(timingMap);

        IReadOnlyList<FloorTiming> source = timingMap.Floors;
        var result = new NativePlaybackTiming[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            FloorTiming timing = source[i];
            result[i] = new NativePlaybackTiming
            {
                EntryTime = timing.EntryTime,
                ExitTime = timing.ExitTime,
                PauseSeconds = timing.PauseSeconds,
                EntryAngle = (float)timing.EntryAngle,
                AngleMoved = (float)timing.AngleMoved,
                Flags = timing.IsCcw ? NativePlaybackTiming.FlagCcw : 0u,
                Reserved = 0u
            };
        }

        return result;
    }
}
