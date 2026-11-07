using System.Collections;
using System.Runtime.InteropServices;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// The static track visual of a floor only changes at ColorTrack events, so
// consecutive floors share one resolved style. Packing that style (string
// parsing) once per run instead of once per floor must give the same output.
internal static class TrackVisualResolvePackingRegression
{
    public static void Run()
    {
        VerifyMatchesPerFloorPacking(pitchPercent: 100.0);
        VerifyMatchesPerFloorPacking(pitchPercent: 150.0);
        VerifyAllocationDoesNotScalePerFloor();
    }

    private static void VerifyMatchesPerFloorPacking(double pitchPercent)
    {
        const int floors = 200;
        LevelDocument level = LevelDocument.CreateSynthetic(floors);
        level.PitchPercent = pitchPercent;
        level.ReplaceActions(
        [
            Action(10, 0, "ColorTrack"),
            Action(20, 1, "ColorTrack"),
            Action(20, 2, "ColorTrack"),
            Action(30, 3, "RecolorTrack"),
            Action(50, 4, "ColorTrack", active: false),
            Action(80, 5, "ColorTrack"),
            Action(81, 6, "ColorTrack"),
            Action(floors - 1, 7, "ColorTrack")
        ]);
        TrackVisualMetadataCache.Attach(level, new TrackVisualSourceData(
            new TrackVisualStyle("Single", "debb7b", "ffffff", 2.0, "None", 4, "Standard", 100.0, string.Empty, 1.0, 0),
            [
                Event(0, 10, "ColorTrack", "Stripes", "ff0000", "00ff00", glow: 40, justThisTile: false),
                Event(1, 20, "ColorTrack", "Glow", "0000ff", null, glow: null, justThisTile: true),
                Event(2, 20, "ColorTrack", null, "112233", null, glow: 75, justThisTile: false),
                Event(3, 30, "RecolorTrack", "Single", "abcdef", "ffffff", glow: null, justThisTile: false),
                Event(4, 50, "ColorTrack", "Rainbow", "ffffff", null, glow: null, justThisTile: false),
                Event(5, 80, "ColorTrack", "Volume", "445566", "778899", glow: 10, justThisTile: true, outlines: true),
                Event(6, 81, "ColorTrack", null, null, null, glow: null, justThisTile: true, style: "Neon"),
                Event(7, floors - 1, "ColorTrack", "Single", "010203", null, glow: null, justThisTile: false)
            ]));

        NativeTrackVisual[] actual = TrackVisualResolver.Resolve(level);
        NativeTrackVisual[] expected = ResolvePerFloor(level);
        if (!StructuralComparisons.StructuralEqualityComparer.Equals(actual, expected))
        {
            int floor = Enumerable.Range(0, floors).First(i => !actual[i].Equals(expected[i]));
            throw new InvalidOperationException(
                $"Track visual at floor {floor} differs from per-floor packing (pitch {pitchPercent}%): " +
                $"actual={actual[floor]}, expected={expected[floor]}.");
        }
    }

    // The resolution as it was before run packing: every floor packs its own
    // style independently.
    private static NativeTrackVisual[] ResolvePerFloor(LevelDocument level)
    {
        TrackVisualSourceData source = TrackVisualMetadataCache.Get(level);
        List<TrackVisualSourceEvent> live = TrackVisualResolver.BuildLiveEvents(level, source.Events);
        var colorEvents = live
            .Where(static item => item.Active && item.EventType == "ColorTrack")
            .GroupBy(static item => item.Floor)
            .ToDictionary(static g => g.Key, static g => g.OrderBy(static item => item.SourceIndex).ToArray());
        double pitch = Math.Max(0.000001, level.PitchPercent * 0.01);
        TrackVisualStyle persistent = source.InitialStyle;
        var output = new NativeTrackVisual[level.FloorCount];
        for (int floor = 0; floor < output.Length; floor++)
        {
            TrackVisualStyle floorStyle = persistent;
            if (colorEvents.TryGetValue(floor, out TrackVisualSourceEvent[]? events))
            {
                foreach (TrackVisualSourceEvent item in events)
                {
                    floorStyle = TrackVisualResolver.ApplyStyle(floorStyle, item, floor);
                    if (!item.JustThisTile)
                        persistent = floorStyle;
                }
            }
            output[floor] = TrackVisualResolver.Pack(floorStyle, pitch);
        }
        return output;
    }

    private static void VerifyAllocationDoesNotScalePerFloor()
    {
        const int floors = 2_000_000;
        LevelDocument level = LevelDocument.CreateSynthetic(floors);
        level.ReplaceActions([Action(100, 0, "ColorTrack"), Action(1_000_000, 1, "ColorTrack")]);
        TrackVisualMetadataCache.Attach(level, new TrackVisualSourceData(
            new TrackVisualStyle("Single", "debb7b", "ffffff", 2.0, "None", 4, "Standard", 100.0, string.Empty, 1.0, 0),
            [
                Event(0, 100, "ColorTrack", "Stripes", "ff0000", "00ff00", glow: 40, justThisTile: false),
                Event(1, 1_000_000, "ColorTrack", "Glow", "0000ff", null, glow: null, justThisTile: false)
            ]));
        TrackVisualResolver.Resolve(LevelDocument.CreateSynthetic(4));

        long before = GC.GetAllocatedBytesForCurrentThread();
        NativeTrackVisual[] result = TrackVisualResolver.Resolve(level);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long outputBytes = (long)floors * Marshal.SizeOf<NativeTrackVisual>();
        if (result[1_500_000].PrimaryColor != result[1_000_000].PrimaryColor)
            throw new InvalidOperationException("Persistent ColorTrack did not carry to later floors.");
        if (allocated > outputBytes + outputBytes / 4 + (1 << 20))
        {
            throw new InvalidOperationException(
                $"Track visual resolve allocated {allocated / 1048576.0:F1}MiB for a " +
                $"{outputBytes / 1048576.0:F1}MiB result; it repacks every floor.");
        }
    }

    private static LevelAction Action(int floor, int sourceIndex, string eventType, bool active = true) =>
        new(floor, eventType, active, null, null, null, null) { SourceIndex = sourceIndex };

    private static TrackVisualSourceEvent Event(
        int sourceIndex,
        int floor,
        string eventType,
        string? colorType,
        string? primary,
        string? secondary,
        double? glow,
        bool justThisTile,
        bool? outlines = null,
        string? style = null) => new(
            sourceIndex, floor, eventType, true,
            colorType, primary, secondary, 1.5, "Forward", 3,
            style, glow, null, null, outlines,
            justThisTile, 0, null, null);
}
