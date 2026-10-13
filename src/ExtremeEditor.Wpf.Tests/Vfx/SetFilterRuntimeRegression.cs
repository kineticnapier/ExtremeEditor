using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SetFilterRuntimeRegression
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
            static action => string.Equals(action.EventType, "SetFilter", StringComparison.Ordinal));

        Check(failures, "SetFilter occurrence timing", () => RequireOccurrenceTiming(level, timing, timeline));
        Check(failures, "SetFilter source semantics", () => RequireSourceSemantics(timeline));
        Check(failures, "managed SetFilter runtime contract", RequireManagedRuntimeContract);
        Check(failures, "renderer-facing SetFilter timeline", RequireRendererContract);
        Check(failures, "native SetFilter runtime contract", RequireNativeContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "SetFilter runtime integration is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        level.InitialBpm = 120;
        level.PitchPercent = 75;

        level.ReplaceActions(
        [
            Filter(1, 0, true, "Grayscale", true, 50, 2.0, 90, "InOutSine", false),
            Filter(1, 1, true, "Aberration", true, 25, 1.0, 180, "OutExpo", false),
            Filter(2, 2, true, "Petals", true, 100, 1.0, 0, "Linear", true),
            Filter(3, 3, true, "Contrast", false, 80, 1.0, 0, "Linear", false),
            Filter(4, 4, false, "Blur", true, 100, 1.0, 0, "Linear", false)
        ]);

        return level;
    }

    private static LevelAction Filter(
        int floor,
        int sourceIndex,
        bool active,
        string filter,
        bool enabled,
        double intensity,
        double duration,
        double angleOffset,
        string ease,
        bool disableOthers)
    {
        return new LevelAction(floor, "SetFilter", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "SetFilter",
                ["active"] = active,
                ["filter"] = filter,
                ["enabled"] = enabled,
                ["intensity"] = intensity,
                ["duration"] = duration,
                ["ease"] = ease,
                ["disableOthers"] = disableOthers,
                ["angleOffset"] = angleOffset
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
            "SetFilter angleOffset trigger time must not include pitch.");

        double expectedDuration = (first.Duration ?? 0) *
            60.0 / (floor.Bpm * Math.Max(0.000001, level.PitchPercent * 0.01));
        AssertNear(expectedDuration, first.DurationSeconds ?? double.NaN,
            "SetFilter generic timeline duration must remain pitch-aware before runtime Hardbake compensation.");
    }

    private static void RequireSourceSemantics(VfxTimeline timeline)
    {
        VfxOccurrence grayscale = ForSource(timeline, 0);
        JsonObject g = grayscale.SourceEvent.PropertyOverrides ?? new JsonObject();
        AssertNear(0.5, ReadDouble(g, "intensity", -1) / 100.0,
            "SetFilter intensity must be interpreted as a percentage.");
        Require(ReadBool(g, "enabled", false),
            "SetFilter enabled=true was not preserved.");
        Require(!ReadBool(g, "disableOthers", true),
            "SetFilter disableOthers=false was not preserved.");

        VfxOccurrence petals = ForSource(timeline, 2);
        JsonObject p = petals.SourceEvent.PropertyOverrides ?? new JsonObject();
        Require(string.Equals(ReadString(p, "filter"), "Petals", StringComparison.Ordinal),
            "Petals special filter name was not preserved.");
        Require(ReadBool(p, "disableOthers", false),
            "Petals disableOthers=true was not preserved.");

        VfxOccurrence disabled = ForSource(timeline, 3);
        JsonObject d = disabled.SourceEvent.PropertyOverrides ?? new JsonObject();
        Require(!ReadBool(d, "enabled", true),
            "enabled=false SetFilter event was not preserved.");
        Require(disabled.DurationSeconds is > 0,
            "enabled=false SetFilter event must retain its tween duration.");

        Require(!ForSource(timeline, 4).Active,
            "active=false SetFilter occurrence was lost before runtime filtering.");
    }

    private static void RequireManagedRuntimeContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type builder = RequireType(assembly, "ExtremeEditor.Wpf.Native.FilterTimelineBuilder");
        Type nativeEvent = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeFilterEvent");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeFilterState");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.FilterStateEvaluator");

        Require(builder.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "FilterTimelineBuilder.Build is missing.");

        foreach (string member in new[]
                 {
                     "StartTime",
                     "DurationSeconds",
                     "Filter",
                     "TargetIntensity",
                     "Enabled",
                     "Ease",
                     "SourceIndex",
                     "Flags"
                 })
        {
            Require(HasMember(nativeEvent, member),
                $"NativeFilterEvent.{member} is missing.");
        }

        Require(HasStaticMember(nativeEvent, "FlagDisableOthers"),
            "SetFilter disableOthers has no managed runtime flag.");
        Require(HasStaticMember(nativeEvent, "FlagUsesLegacyTween"),
            "SetFilter legacyTween mode has no managed runtime flag.");
    }

    private static void RequireRendererContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type prepared = RequireType(assembly, "ExtremeEditor.Wpf.Native.PreparedNativePlayback");
        Require(prepared.GetProperty("FilterTimeline") is not null,
            "PreparedNativePlayback.FilterTimeline is missing.");

        Type session = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeRendererSession");
        Require(session.GetMethod(
            "SetFilterTimeline",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "NativeRendererSession.SetFilterTimeline is missing.");
    }

    private static void RequireNativeContract()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"));

        foreach (string token in new[]
                 {
                     "EeFilterEvent",
                     "ee_renderer_set_filter_timeline",
                     "EE_FILTER_DISABLE_OTHERS",
                     "EE_FILTER_LEGACY_TWEEN"
                 })
        {
            Require(header.Contains(token, StringComparison.Ordinal),
                $"native SetFilter ABI token '{token}' is missing.");
        }

        string sources = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "src"), "*.cpp")
            .Select(File.ReadAllText));

        foreach (string token in new[]
                 {
                     "ResolveFilterStateAtTime",
                     "SetFilterState",
                     "ApplyFilterIntensity"
                 })
        {
            Require(sources.Contains(token, StringComparison.Ordinal),
                $"native SetFilter runtime contract '{token}' is missing.");
        }
    }

    private static bool HasMember(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static bool HasStaticMember(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
        type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static string? ReadString(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;

    private static bool ReadBool(JsonObject source, string key, bool fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out bool result)
            ? result
            : fallback;

    private static double ReadDouble(JsonObject source, string key, double fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out double result)
            ? result
            : fallback;

    private static VfxOccurrence ForSource(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(item => item.SourceIndex == sourceIndex);

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException(
            $"production type '{name}' is missing.");

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
