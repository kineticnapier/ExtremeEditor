using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class FlashRuntimeRegression
{
    private const double Tolerance = 1e-6;

    public static void Run()
    {
        var failures = new List<string>();
        LevelDocument level = CreateFixture();
        TimingMap timing = TimingMapBuilder.Build(level);
        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level,
            timing,
            static action => string.Equals(action.EventType, "Flash", StringComparison.Ordinal));

        Check(failures, "Flash occurrence timing", () => RequireOccurrenceTiming(level, timing, timeline));
        Check(failures, "Stock plane/channel semantics", () => RequireStockSemantics(timeline));
        Check(failures, "managed Flash runtime contract", RequireManagedRuntimeContract);
        Check(failures, "renderer-facing Flash timeline", RequireRendererContract);
        Check(failures, "native foreground/background Flash layers", RequireNativeContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Flash runtime integration is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        level.InitialBpm = 120;
        level.PitchPercent = 75;

        level.ReplaceActions(
        [
            Flash(1, 0, true, "Background", "ff0000", 100, "0000ff", 0, 1.0, 0, "Linear"),
            Flash(1, 1, true, "Foreground", "00ff00", 80, "000000", 0, 2.0, 0, "Linear"),
            Flash(1, 2, true, "Background", "ffffff", 50, "101010", 25, 1.0, 90, "Linear"),
            Flash(2, 3, false, "Foreground", "ff00ff", 100, "ff00ff", 100, 1.0, 0, "Linear"),
            Flash(3, 4, true, "Foreground", "112233", 60, "445566", 20, 0.0, 0, "Linear")
        ]);

        return level;
    }

    private static LevelAction Flash(
        int floor,
        int sourceIndex,
        bool active,
        string plane,
        string startColor,
        double startOpacity,
        string endColor,
        double endOpacity,
        double duration,
        double angleOffset,
        string ease)
    {
        return new LevelAction(floor, "Flash", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "Flash",
                ["active"] = active,
                ["plane"] = plane,
                ["startColor"] = startColor,
                ["startOpacity"] = startOpacity,
                ["endColor"] = endColor,
                ["endOpacity"] = endOpacity,
                ["duration"] = duration,
                ["angleOffset"] = angleOffset,
                ["ease"] = ease
            }
        };
    }

    private static void RequireOccurrenceTiming(
        LevelDocument level,
        TimingMap timing,
        VfxTimeline timeline)
    {
        VfxOccurrence first = ForSource(timeline, 0);
        FloorTiming floor = timing.Floors[first.Floor];

        double expectedStart = floor.EntryTime +
            (first.AngleOffset ?? 0) / 180.0 * (60.0 / floor.Bpm);
        AssertNear(expectedStart, first.StartTime,
            "Flash angleOffset trigger time does not match stock timing.");

        // VfxTimeline's generic duration is pitch-aware. Stock Flash additionally
        // performs its Hardbake compensation only when the stock runtime requests it;
        // that renderer/runtime-specific adjustment must not be guessed here.
        double expectedDuration = (first.Duration ?? 0) *
            60.0 / (floor.Bpm * Math.Max(0.000001, level.PitchPercent * 0.01));
        AssertNear(expectedDuration, first.DurationSeconds ?? double.NaN,
            "Flash generic duration seconds are not pitch-aware.");

        if (!timeline.Occurrences.Any(static item => item.SourceIndex == 3 && !item.Active))
            throw new InvalidOperationException("active:false Flash occurrence was lost before runtime filtering.");
    }

    private static void RequireStockSemantics(VfxTimeline timeline)
    {
        VfxOccurrence backgroundA = ForSource(timeline, 0);
        VfxOccurrence foreground = ForSource(timeline, 1);
        VfxOccurrence backgroundB = ForSource(timeline, 2);
        VfxOccurrence instant = ForSource(timeline, 4);

        FlashSpecState before = Evaluate(timeline, backgroundA.StartTime - 0.0001);
        Require(before.Background.Opacity == 0 && before.Foreground.Opacity == 0,
            "Flash state before first event was not transparent.");

        FlashSpecState atBackground = Evaluate(timeline, backgroundA.StartTime);
        Require(atBackground.Background.Color == "ff0000" &&
                Near(atBackground.Background.Opacity, 1.0),
            "Background Flash did not apply startColor/startOpacity at trigger.");

        double halfA = backgroundA.StartTime + (backgroundA.DurationSeconds ?? 0) * 0.5;
        FlashSpecState midway = Evaluate(timeline, halfA);
        Require(midway.Background.Color == "800080" &&
                Near(midway.Background.Opacity, 0.5),
            "Background Flash did not interpolate independently.");

        FlashSpecState foregroundStart = Evaluate(timeline, foreground.StartTime);
        Require(foregroundStart.Foreground.Color == "00ff00" &&
                Near(foregroundStart.Foreground.Opacity, 0.8),
            "Foreground Flash did not use an independent channel.");

        FlashSpecState replacementStart = Evaluate(timeline, backgroundB.StartTime);
        Require(replacementStart.Background.Color == "ffffff" &&
                Near(replacementStart.Background.Opacity, 0.5),
            "Later same-plane Flash did not replace the previous completed channel with its own start value.");

        FlashSpecState afterInactive = Evaluate(timeline, ForSource(timeline, 3).StartTime);
        Require(afterInactive.Foreground.Color != "ff00ff",
            "active:false Flash changed the effective foreground state.");

        FlashSpecState atInstant = Evaluate(timeline, instant.StartTime);
        Require(atInstant.Foreground.Color == "445566" &&
                Near(atInstant.Foreground.Opacity, 0.2),
            "duration=0 Flash did not immediately complete to its end state.");
    }

    private static void RequireManagedRuntimeContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        RequireType(assembly, "ExtremeEditor.Wpf.Native.FlashTimelineBuilder");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.FlashStateEvaluator");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeFlashEvent");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeFlashState");
    }

    private static void RequireRendererContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type prepared = RequireType(assembly, "ExtremeEditor.Wpf.Native.PreparedNativePlayback");
        Require(prepared.GetProperty("FlashTimeline") is not null,
            "PreparedNativePlayback.FlashTimeline is missing.");

        Type session = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeRendererSession");
        Require(session.GetMethod(
            "SetFlashTimeline",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "NativeRendererSession.SetFlashTimeline is missing.");
    }

    private static void RequireNativeContract()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"));
        foreach (string token in new[]
                 {
                     "EeFlashEvent",
                     "ee_renderer_set_flash_timeline",
                     "EE_FLASH_PLANE_BACKGROUND",
                     "EE_FLASH_PLANE_FOREGROUND"
                 })
        {
            Require(header.Contains(token, StringComparison.Ordinal),
                $"native Flash ABI token '{token}' is missing.");
        }

        string sources = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "src"), "*.cpp")
            .Select(File.ReadAllText));

        foreach (string token in new[]
                 {
                     "ResolveFlashAtTime",
                     "DrawBackgroundFlash",
                     "DrawForegroundFlash"
                 })
        {
            Require(sources.Contains(token, StringComparison.Ordinal),
                $"native Flash renderer contract '{token}' is missing.");
        }
    }

    private static FlashSpecState Evaluate(VfxTimeline timeline, double chartTime)
    {
        FlashColor background = FlashColor.Transparent;
        FlashColor foreground = FlashColor.Transparent;

        foreach (VfxOccurrence occurrence in timeline.Occurrences
                     .Where(static item => item.EventType == "Flash")
                     .OrderBy(static item => item.StartTime - item.StartEffectOffset)
                     .ThenBy(static item => item.Floor))
        {
            if (!occurrence.Active || occurrence.StartTime > chartTime)
                continue;

            JsonObject properties = occurrence.SourceEvent.PropertyOverrides ?? new JsonObject();
            bool isForeground = string.Equals(
                ReadString(properties, "plane", "Background"),
                "Foreground",
                StringComparison.OrdinalIgnoreCase);

            FlashColor value = ResolveOccurrence(occurrence, properties, chartTime);
            if (isForeground)
                foreground = value;
            else
                background = value;
        }

        return new FlashSpecState(background, foreground);
    }

    private static FlashColor ResolveOccurrence(
        VfxOccurrence occurrence,
        JsonObject properties,
        double chartTime)
    {
        FlashColor start = new(
            ReadString(properties, "startColor", "ffffff"),
            ReadDouble(properties, "startOpacity", 100) / 100.0);
        FlashColor end = new(
            ReadString(properties, "endColor", "ffffff"),
            ReadDouble(properties, "endOpacity", 0) / 100.0);

        double duration = Math.Max(0, occurrence.DurationSeconds ?? 0);
        if (duration <= Tolerance)
            return end;

        double progress = Math.Clamp((chartTime - occurrence.StartTime) / duration, 0, 1);
        return Lerp(start, end, progress);
    }

    private static FlashColor Lerp(FlashColor start, FlashColor end, double t)
    {
        (int sr, int sg, int sb) = ParseRgb(start.Color);
        (int er, int eg, int eb) = ParseRgb(end.Color);
        int r = ClampByte(sr + (er - sr) * t);
        int g = ClampByte(sg + (eg - sg) * t);
        int b = ClampByte(sb + (eb - sb) * t);
        return new FlashColor(
            $"{r:x2}{g:x2}{b:x2}",
            start.Opacity + (end.Opacity - start.Opacity) * t);
    }

    private static (int R, int G, int B) ParseRgb(string color)
    {
        string normalized = color.Trim().TrimStart('#');
        if (normalized.Length < 6)
            normalized = normalized.PadRight(6, '0');
        return (
            Convert.ToInt32(normalized[..2], 16),
            Convert.ToInt32(normalized.Substring(2, 2), 16),
            Convert.ToInt32(normalized.Substring(4, 2), 16));
    }

    private static int ClampByte(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static VfxOccurrence ForSource(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(item => item.SourceIndex == sourceIndex);

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException(
            $"production type '{name}' is missing.");

    private static string ReadString(JsonObject source, string key, string fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? fallback
            : fallback;

    private static double ReadDouble(JsonObject source, string key, double fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out double result)
            ? result
            : fallback;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("repository root was not found.");
    }

    private static bool Near(double left, double right) =>
        Math.Abs(left - right) <= Tolerance;

    private static void AssertNear(double expected, double actual, string message)
    {
        if (!Near(expected, actual))
            throw new InvalidOperationException(
                $"{message} expected={expected:R} actual={actual:R}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Check(List<string> failures, string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { failures.Add($"{name}: {Unwrap(ex).Message}"); }
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception;
    }

    private readonly record struct FlashColor(string Color, double Opacity)
    {
        public static FlashColor Transparent => new("000000", 0);
    }

    private readonly record struct FlashSpecState(
        FlashColor Background,
        FlashColor Foreground);
}
