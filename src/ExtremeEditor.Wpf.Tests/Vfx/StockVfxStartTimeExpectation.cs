using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Expected ffxPlusBase.SetStartTime on stock ADOFAI's Mono, which runs with
/// "-O=-float32": float operands, double intermediates, on the stock entry
/// time clock the timing builders stamp on every floor.
/// </summary>
internal static class StockVfxStartTimeExpectation
{
    public static double For(FloorTiming floor, double angleOffset)
    {
        double entry = floor.StockEntryTime
            ?? throw new InvalidOperationException("Timing builder did not provide the stock VFX clock.");
        double bpm = floor.StockBpm
            ?? throw new InvalidOperationException("Timing builder did not provide the stock bpm.");
        if (Math.Abs(bpm - floor.Bpm) > floor.Bpm * 1e-6 || Math.Abs(entry - floor.EntryTime) > 1e-6)
        {
            throw new InvalidOperationException(
                $"Stock VFX clock {entry:R}/{bpm:R} drifted from playback {floor.EntryTime:R}/{floor.Bpm:R}.");
        }

        float degrees = (float)angleOffset;
        return entry + (double)degrees * (double)0.017453292f / (double)3.1415927f * (60.0 / bpm);
    }
}
