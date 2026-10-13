using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class BloomRuntimeRegression
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
            static action => string.Equals(action.EventType, "Bloom", StringComparison.Ordinal));

        Check(failures, "Bloom occurrence timing", () => RequireOccurrenceTiming(level, timing, timeline));
        Check(failures, "Bloom stock source semantics", () => RequireStockSourceSemantics(timeline));
        Check(failures, "managed Bloom runtime contract", RequireManagedRuntimeContract);
        Check(failures, "renderer-facing Bloom timeline", RequireRendererContract);
        Check(failures, "native Bloom runtime contract", RequireNativeContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Bloom runtime integration is not available:\n- " +
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
            Bloom(1, 0, active: true, enabled: true, threshold: 40, intensity: 80,
                color: "ff8040", duration: 1.0, angleOffset: 90, ease: "Linear"),
            Bloom(2, 1, active: true, enabled: false, threshold: 20, intensity: 30,
                color: "204060", duration: 2.0, angleOffset: 0, ease: "OutSine"),
            Bloom(3, 2, active: false, enabled: true, threshold: 99, intensity: 99,
                color: "ffffff", duration: 1.0, angleOffset: 0, ease: "Linear")
        ]);

        return level;
    }

    private static LevelAction Bloom(
        int floor,
        int sourceIndex,
        bool active,
        bool enabled,
        double threshold,
        double intensity,
        string color,
        double duration,
        double angleOffset,
        string ease)
    {
        return new LevelAction(floor, "Bloom", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "Bloom",
                ["active"] = active,
                ["enabled"] = enabled,
                ["threshold"] = threshold,
                ["intensity"] = intensity,
                ["color"] = color,
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
            "Bloom angleOffset trigger time must not include pitch.");

        double expectedDuration = (first.Duration ?? 0) *
            60.0 / (floor.Bpm * Math.Max(0.000001, level.PitchPercent * 0.01));
        AssertNear(expectedDuration, first.DurationSeconds ?? double.NaN,
            "Bloom duration must use pitch-aware crotchet timing without an extra Hardbake correction.");
    }

    private static void RequireStockSourceSemantics(VfxTimeline timeline)
    {
        VfxOccurrence enabled = ForSource(timeline, 0);
        VfxOccurrence disabled = ForSource(timeline, 1);
        VfxOccurrence inactive = ForSource(timeline, 2);

        JsonObject enabledProps = enabled.SourceEvent.PropertyOverrides ?? new JsonObject();
        AssertNear(0.4, ReadDouble(enabledProps, "threshold", -1) / 100.0,
            "Bloom threshold must be interpreted as a percentage.");
        AssertNear(0.8, ReadDouble(enabledProps, "intensity", -1) / 100.0,
            "Bloom intensity must be interpreted as a percentage.");

        JsonObject disabledProps = disabled.SourceEvent.PropertyOverrides ?? new JsonObject();
        Require(ReadBool(disabledProps, "enabled", true) == false,
            "disabled Bloom event lost its enabled=false state.");
        Require(disabled.DurationSeconds is > 0,
            "disabled Bloom must retain its numeric tween duration.");

        Require(!inactive.Active,
            "active=false Bloom occurrence was lost before runtime filtering.");
    }

    private static void RequireManagedRuntimeContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type builder = RequireType(assembly, "ExtremeEditor.Wpf.Native.BloomTimelineBuilder");
        Type nativeEvent = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeBloomEvent");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeBloomState");

        Require(builder.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "BloomTimelineBuilder.Build is missing.");

        foreach (string member in new[]
                 {
                     "StartTime",
                     "DurationSeconds",
                     "Threshold",
                     "Intensity",
                     "Tint",
                     "Enabled",
                     "Ease",
                     "SourceIndex"
                 })
        {
            Require(HasMember(nativeEvent, member),
                $"NativeBloomEvent.{member} is missing.");
        }
    }

    private static void RequireRendererContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type prepared = RequireType(assembly, "ExtremeEditor.Wpf.Native.PreparedNativePlayback");
        Require(prepared.GetProperty("BloomTimeline") is not null,
            "PreparedNativePlayback.BloomTimeline is missing.");

        Type session = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeRendererSession");
        Require(session.GetMethod(
            "SetBloomTimeline",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "NativeRendererSession.SetBloomTimeline is missing.");
    }

    private static void RequireNativeContract()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"));

        foreach (string token in new[]
                 {
                     "EeBloomEvent",
                     "ee_renderer_set_bloom_timeline"
                 })
        {
            Require(header.Contains(token, StringComparison.Ordinal),
                $"native Bloom ABI token '{token}' is missing.");
        }

        string sources = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "src"), "*.cpp")
            .Select(File.ReadAllText));

        Require(sources.Contains("ResolveBloomAtTime", StringComparison.Ordinal),
            "native Bloom deterministic evaluator is missing.");
        Require(sources.Contains("SetBloomState", StringComparison.Ordinal),
            "native renderer does not pass resolved Bloom state into the rendering backend.");
    }

    private static bool HasMember(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static VfxOccurrence ForSource(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(item => item.SourceIndex == sourceIndex);

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException(
            $"production type '{name}' is missing.");

    private static double ReadDouble(JsonObject source, string key, double fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out double result)
            ? result
            : fallback;

    private static bool ReadBool(JsonObject source, string key, bool fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out bool result)
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

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > Tolerance)
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
}
