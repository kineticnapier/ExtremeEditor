using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class PlaybackVisibleFloorSelectorRegression
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    public static void Run()
    {
        VerifyOrdinaryWindow();
        VerifyEqualTimesAreInclusive();
        VerifyChartBoundaries();
        VerifyLargeMapReturnsNarrowRange();
    }

    private static void VerifyOrdinaryWindow()
    {
        TimingMap map = CreateMap([0.0, 1.0, 2.0, 3.0, 4.0]);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 2.0, 0.5, 1.0);
        if (range.StartFloor != 2 || range.EndExclusive != 4)
        {
            throw new InvalidOperationException(
                $"Expected [2,4), actual=[{range.StartFloor},{range.EndExclusive}).");
        }
    }

    private static void VerifyEqualTimesAreInclusive()
    {
        TimingMap map = CreateMap([0.0, 1.0, 1.0, 1.0, 2.0]);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 1.0, 0.0, 0.0);
        if (range.StartFloor != 1 || range.EndExclusive != 4)
        {
            throw new InvalidOperationException(
                $"Equal-time floors must all be selected. actual=[{range.StartFloor},{range.EndExclusive}).");
        }
    }

    private static void VerifyChartBoundaries()
    {
        TimingMap map = CreateMap([0.0, 1.0, 2.0]);
        PlaybackFloorRange before = PlaybackVisibleFloorSelector.Select(map, -5.0, 0.1, 0.1);
        PlaybackFloorRange after = PlaybackVisibleFloorSelector.Select(map, 10.0, 0.1, 0.1);
        if (before.Count != 0 || after.Count != 0)
        {
            throw new InvalidOperationException(
                $"Far-outside windows must be empty. before={before.Count}, after={after.Count}.");
        }
    }

    private static void VerifyLargeMapReturnsNarrowRange()
    {
        const int count = 200_000;
        var floors = new FloorTiming[count];
        for (int i = 0; i < count; i++)
        {
            floors[i] = new FloorTiming(
                i,
                i * 0.001,
                i * 0.0015,
                0,
                0,
                Math.PI,
                120,
                false,
                false);
        }

        var map = new TimingMap(floors);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 100.0, 0.010, 0.010);
        if (range.Count > 25)
        {
            throw new InvalidOperationException(
                $"A narrow temporal query must stay narrow on a large map. count={range.Count}.");
        }
    }

    private static TimingMap CreateMap(double[] entryTimes)
    {
        var floors = new FloorTiming[entryTimes.Length];
        for (int i = 0; i < entryTimes.Length; i++)
        {
            floors[i] = new FloorTiming(
                i,
                entryTimes[i],
                entryTimes[i] + 0.5,
                0,
                0,
                Math.PI,
                120,
                false,
                false);
        }

        return new TimingMap(floors);
    }
}
