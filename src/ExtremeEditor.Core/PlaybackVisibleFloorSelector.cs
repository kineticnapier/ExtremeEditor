namespace ExtremeEditor.Core;

public readonly record struct PlaybackFloorRange(int StartFloor, int EndExclusive)
{
    public int Count => Math.Max(0, EndExclusive - StartFloor);
}

public static class PlaybackVisibleFloorSelector
{
    public static PlaybackFloorRange Select(
        TimingMap timingMap,
        double chartTime,
        double pastWindowSeconds,
        double futureWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        if (pastWindowSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(pastWindowSeconds));
        if (futureWindowSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(futureWindowSeconds));

        double minTime = chartTime - pastWindowSeconds;
        double maxTime = chartTime + futureWindowSeconds;
        int start = timingMap.FindFirstFloorAtOrAfter(minTime);
        int endExclusive = timingMap.FindFirstFloorAfter(maxTime);
        if (endExclusive < start)
            endExclusive = start;

        return new PlaybackFloorRange(start, endExclusive);
    }
}
