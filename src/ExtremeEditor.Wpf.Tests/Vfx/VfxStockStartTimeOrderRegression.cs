using System.IO;
using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Stock ADOFAI runs its scripts on Mono with "-O=-float32" (UnityPlayer.dll),
/// so float32 expressions in scrLevelMaker.CalculateFloorEntryTimes and
/// ffxPlusBase.SetStartTime are evaluated in double precision and rounded only
/// when stored into a float. scnGame.PrepVfx then orders effects by
/// (startTime - startEffectOffset, floor.seqID) and scrVfxPlus.Update starts
/// them in that order. A 360 degree event on one floor and an angleOffset 0
/// event two beats later are only nanoseconds apart, so the stock arithmetic
/// decides which one runs first (Phantigma lost.png, floors 53 and 55).
/// </summary>
internal static class VfxStockStartTimeOrderRegression
{
    // Measured by running these exact stock expressions on ADOFAI's
    // MonoBleedingEdge mono-2.0-bdwgc.dll with "-O=-float32".
    private const double StockFloor2At0 = 1.2142857200452981;
    private const double StockFloor2At90 = 1.2500000044890873;
    private const double StockFloor2At180 = 1.2857142889328765;
    private const double StockFloor2At270 = 1.3214285733766657;
    private const double StockFloor2At360 = 1.3571428578204552;
    private const double StockFloor4At0 = 1.3571428629024409;

    private const double Beat = 60.0 / 840.0;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VFX_STOCK_START_ORDER_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        LevelDocument level = LoadChart();
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence[] blink = timeline.Occurrences
            .Where(item => item.EventType == "MoveDecorations" && item.EventTag == "blink")
            .ToArray();
        VfxOccurrence[] floor2 = blink.Where(item => item.Floor == 2).ToArray();
        VfxOccurrence fade = blink.Single(item => item.Floor == 4);

        RequireExact(floor2.Single(item => item.AngleOffset == 0).StartTime, StockFloor2At0, "floor 2 angleOffset 0");
        RequireExact(floor2.Single(item => item.AngleOffset == 90).StartTime, StockFloor2At90, "floor 2 angleOffset 90");
        RequireExact(floor2.Single(item => item.AngleOffset == 180).StartTime, StockFloor2At180, "floor 2 angleOffset 180");
        RequireExact(floor2.Single(item => item.AngleOffset == 270).StartTime, StockFloor2At270, "floor 2 angleOffset 270");
        VfxOccurrence at360 = floor2.Single(item => item.AngleOffset == 360);
        RequireExact(at360.StartTime, StockFloor2At360, "floor 2 angleOffset 360");
        RequireExact(fade.StartTime, StockFloor4At0, "floor 4 angleOffset 0");

        int at360Index = Array.IndexOf(timeline.Occurrences.ToArray(), at360);
        int fadeIndex = Array.IndexOf(timeline.Occurrences.ToArray(), fade);
        if (!(at360Index < fadeIndex))
        {
            throw new InvalidOperationException(
                $"angleOffset 360 (index {at360Index}) must start before the next floor's fade (index {fadeIndex}).");
        }

        LevelDecoration blinkDecoration = level.Decorations.Single(item =>
            item.Properties["tag"]?.ToString() == "blink");
        double midFade = fade.StartTime + 7 * Beat;
        double afterFade = fade.StartTime + 14 * Beat;

        // Direct seeks: blink sequence, the nanosecond window, the fade and after it.
        RequireOpacity(blinkDecoration, timeline, StockFloor2At0 + 0.001, 100, "floor 2 angleOffset 0");
        RequireOpacity(blinkDecoration, timeline, StockFloor2At90 + 0.001, 0, "floor 2 angleOffset 90");
        RequireOpacity(blinkDecoration, timeline, StockFloor2At180 + 0.001, 100, "floor 2 angleOffset 180");
        RequireOpacity(blinkDecoration, timeline, StockFloor2At270 + 0.001, 0, "floor 2 angleOffset 270");
        RequireOpacity(blinkDecoration, timeline, (StockFloor2At360 + StockFloor4At0) * 0.5, 100,
            "between the 360 degree event and the fade");
        // InCubic at half time is 0.125, from 100 towards 0.
        RequireOpacity(blinkDecoration, timeline, midFade, 87.5, "mid fade");
        RequireOpacity(blinkDecoration, timeline, afterFade, 0, "fade end");
        RequireOpacity(blinkDecoration, timeline, afterFade + 10.0, 0, "after the fade");

        // Reverse seek and repeated evaluation at one time are stateless.
        RequireOpacity(blinkDecoration, timeline, midFade, 87.5, "reverse seek to mid fade");
        RequireOpacity(blinkDecoration, timeline, midFade, 87.5, "repeated evaluation");
        RequireOpacity(blinkDecoration, timeline, StockFloor2At180 + 0.001, 100, "reverse seek to the blink");

        // Continuous playback at 60 fps: once the fade has started the opacity
        // only falls, and it ends at the fade target.
        double previous = double.PositiveInfinity;
        for (double time = fade.StartTime; time <= afterFade + 0.5; time += 1.0 / 60.0)
        {
            double opacity = DecorationState.Evaluate(blinkDecoration, timeline, time).Opacity;
            if (opacity > previous + 1e-9)
            {
                throw new InvalidOperationException(
                    $"continuous playback rose from {previous} to {opacity} at {time:R}.");
            }
            previous = opacity;
        }
        RequireNear(previous, 0, "continuous playback end");

        // Kill(true): a new opacity tween first completes the running fade at
        // its end value (0), then tweens from there instead of from the
        // interpolated value.
        LevelDecoration interrupted = level.Decorations.Single(item =>
            item.Properties["tag"]?.ToString() == "interrupt");
        VfxOccurrence interrupt = timeline.Occurrences.Single(item =>
            item.EventTag == "interrupt" && item.AngleOffset == 90);
        RequireOpacity(interrupted, timeline, interrupt.StartTime - 1e-6, 99.9, "interrupted fade before the interrupt", tolerance: 0.1);
        RequireOpacity(interrupted, timeline, interrupt.StartTime + Beat, 25, "interrupt tween midpoint");
        RequireOpacity(interrupted, timeline, interrupt.StartTime + 2 * Beat, 50, "interrupt tween end");
        RequireOpacity(interrupted, timeline, interrupt.StartTime + 100, 50, "state kept after the interrupt");

        Console.WriteLine(
            $"PASS: stock VFX start order (360 at {at360.StartTime:R} before fade at {fade.StartTime:R}).");
    }

    private static LevelDocument LoadChart()
    {
        // Floor 2 turns 30 degrees and floor 3 turns 330 degrees, so floor 4
        // starts exactly two beats (= 360 degrees) after floor 2.
        object[] decorations =
        [
            Decoration("blink"),
            Decoration("interrupt")
        ];
        object[] actions =
        [
            new { floor = 1, eventType = "SetSpeed", speedType = "Multiplier", beatsPerMinute = 100, bpmMultiplier = 4, angleOffset = 0 },
            Move(2, "blink", 100, 0, 0, "Linear"),
            Move(2, "blink", 0, 90, 0, "Linear"),
            Move(2, "blink", 100, 180, 0, "Linear"),
            Move(2, "blink", 0, 270, 0, "Linear"),
            Move(2, "blink", 100, 360, 0, "Linear"),
            Move(4, "blink", 0, 0, 14, "InCubic"),
            Move(4, "interrupt", 100, 0, 0, "Linear"),
            Move(4, "interrupt", 0, 0, 14, "InCubic"),
            Move(4, "interrupt", 50, 90, 2, "Linear")
        ];

        string json = JsonSerializer.Serialize(new
        {
            angleData = new double[] { 0, 0, 150, 0, 0, 0 },
            settings = new
            {
                bpm = 210,
                offset = 0,
                pitch = 100,
                countdownTicks = 4,
                separateCountdownTime = true,
                hitsound = "Kick",
                hitsoundVolume = 100
            },
            actions,
            decorations
        });
        string path = Path.Combine(
            Path.GetTempPath(), $"extremeeditor-vfx-stock-order-{Guid.NewGuid():N}.adofai");
        try
        {
            File.WriteAllText(path, json);
            return AdoFaiLoader.Load(path).Document;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static object Decoration(string tag) => new
    {
        floor = 2,
        eventType = "AddDecoration",
        decorationImage = "",
        tag,
        relativeTo = "Camera",
        position = new[] { 0, 0 },
        scale = new[] { 100, 100 },
        opacity = 0,
        depth = 0
    };

    private static object Move(int floor, string tag, double opacity, double angleOffset, double duration, string ease) => new
    {
        floor,
        eventType = "MoveDecorations",
        duration,
        tag,
        opacity,
        angleOffset,
        ease,
        eventTag = tag
    };

    private static void RequireExact(double actual, double expected, string label)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{label} start time {actual:R} differs from stock {expected:R} ({actual - expected:E3} s).");
        }
    }

    private static void RequireOpacity(
        LevelDecoration decoration,
        VfxTimeline timeline,
        double time,
        double expected,
        string label,
        double tolerance = 1e-6)
    {
        double opacity = DecorationState.Evaluate(decoration, timeline, time).Opacity;
        if (Math.Abs(opacity - expected) > tolerance)
        {
            throw new InvalidOperationException(
                $"{label}: opacity at {time:R} was {opacity}, expected {expected}.");
        }
    }

    private static void RequireNear(double actual, double expected, string label)
    {
        if (Math.Abs(actual - expected) > 1e-6)
            throw new InvalidOperationException($"{label}: {actual}, expected {expected}.");
    }
}
