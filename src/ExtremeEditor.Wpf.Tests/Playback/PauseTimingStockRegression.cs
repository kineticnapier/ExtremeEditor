using System.IO;
using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class PauseTimingStockRegression
{
    private const double PiStock = 3.1415927410125732;

    public static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-PauseTimingStock-{Guid.NewGuid():N}.adofai");

        try
        {
            double[] angles = new double[66];
            angles[60] = -195.0;
            angles[62] = -195.0;

            var fixture = new
            {
                angleData = angles,
                settings = new
                {
                    bpm = 180,
                    countdownTicks = 4,
                    separateCountdownTime = true,
                    offset = 1660,
                    pitch = 100,
                    hitsound = "Kick",
                    hitsoundVolume = 100
                },
                actions = new object[]
                {
                    new
                    {
                        floor = 61,
                        eventType = "Pause",
                        duration = 4.0,
                        countdownTicks = 3,
                        angleCorrectionDir = -1
                    },
                    new
                    {
                        floor = 63,
                        eventType = "Pause",
                        duration = 6.95,
                        countdownTicks = 3,
                        angleCorrectionDir = -1
                    },
                    new
                    {
                        floor = 63,
                        eventType = "SetSpeed",
                        speedType = "Bpm",
                        beatsPerMinute = 230.0,
                        angleOffset = 0
                    }
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(fixture));
            LevelDocument level = AdoFaiLoader.Load(path).Document;

            VerifyBuilder("TimingMapBuilder", TimingMapBuilder.Build(level));
            VerifyBuilder("FlatTimingMapBuilder", FlatTimingMapBuilder.Build(level));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void VerifyBuilder(string builder, TimingMap timing)
    {
        // Assembly-CSharp.dll (scnGame.ApplyCoreEventsToFloors) records Pause.duration
        // as extraBeats on the event floor. scrLevelMaker.CalculateFloorEntryTimes then
        // multiplies both the normal angle and extraBeats by that floor's final speed.
        // Therefore a same-floor angleOffset=0 SetSpeed applies regardless of JSON order.
        Near(
            4.0 * 60.0 / 180.0,
            timing.Floors[61].PauseSeconds,
            builder,
            floor: 61,
            component: "PauseSeconds");

        double stockPauseSeconds = 6.95 * 60.0 / 230.0;
        Near(
            stockPauseSeconds,
            timing.Floors[63].PauseSeconds,
            builder,
            floor: 63,
            component: "PauseSeconds");

        FloorTiming floor63 = timing.Floors[63];
        double stockInterval =
            stockPauseSeconds + floor63.AngleMoved / PiStock * (60.0 / 230.0);
        Near(
            stockInterval,
            timing.GetEntryTime(64) - timing.GetEntryTime(63),
            builder,
            floor: 63,
            component: "entry-to-next-entry interval");
    }

    private static void Near(
        double expected,
        double actual,
        string builder,
        int floor,
        string component)
    {
        if (Math.Abs(expected - actual) > 1e-9)
        {
            throw new InvalidOperationException(
                $"Pause timing does not match stock at floor {floor} ({builder} {component}): " +
                $"expected {expected:R}, actual {actual:R}.");
        }
    }
}
