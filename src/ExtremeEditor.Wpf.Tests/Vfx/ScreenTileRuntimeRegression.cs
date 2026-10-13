using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class ScreenTileRuntimeRegression
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
            static action => string.Equals(action.EventType, "ScreenTile", StringComparison.Ordinal));

        Check(failures, "ScreenTile occurrence timing", () => RequireOccurrenceTiming(level, timing, timeline));
        Check(failures, "ScreenTile stock source semantics", () => RequireStockSourceSemantics(timeline));
        Check(failures, "managed ScreenTile runtime contract", RequireManagedRuntimeContract);
        Check(failures, "renderer-facing ScreenTile timeline", RequireRendererContract);
        Check(failures, "native ScreenTile runtime contract", RequireNativeContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "ScreenTile runtime integration is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(7);
        level.InitialBpm = 120;
        level.PitchPercent = 75;

        level.ReplaceActions(
        [
            ScreenTile(1, 0, active: true, tileX: 2.0, tileY: 0.5, duration: 2.0, angleOffset: 90, ease: "InOutSine"),
            ScreenTile(1, 1, active: true, tileX: 0.0, tileY: 1.0, duration: 0.0, angleOffset: 180, ease: "Linear"),
            ScreenTile(2, 2, active: true, tileX: 1.0, tileY: 1.0, duration: 1.0, angleOffset: 0, ease: "OutExpo"),
            ScreenTile(3, 3, active: false, tileX: 3.0, tileY: 3.0, duration: 1.0, angleOffset: 0, ease: "Linear")
        ]);

        return level;
    }

    private static LevelAction ScreenTile(
        int floor,
        int sourceIndex,
        bool active,
        double tileX,
        double tileY,
        double duration,
        double angleOffset,
        string ease)
    {
        return new LevelAction(floor, "ScreenTile", active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "ScreenTile",
                ["active"] = active,
                ["duration"] = duration,
                ["tile"] = new JsonArray(tileX, tileY),
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
            "ScreenTile angleOffset trigger time must not include pitch.");

        double expectedDuration = (first.Duration ?? 0) *
            60.0 / (floor.Bpm * Math.Max(0.000001, level.PitchPercent * 0.01));
        AssertNear(expectedDuration, first.DurationSeconds ?? double.NaN,
            "ScreenTile duration must use pitch-aware crotchet timing without Hardbake correction.");
    }

    private static void RequireStockSourceSemantics(VfxTimeline timeline)
    {
        VfxOccurrence tween = ForSource(timeline, 0);
        VfxOccurrence instant = ForSource(timeline, 1);
        VfxOccurrence identity = ForSource(timeline, 2);
        VfxOccurrence inactive = ForSource(timeline, 3);

        (double tweenX, double tweenY) = ReadTile(tween);
        AssertNear(2.0, tweenX, "ScreenTile tile[0] was not preserved.");
        AssertNear(0.5, tweenY, "ScreenTile tile[1] was not preserved.");

        Require((instant.DurationSeconds ?? -1) <= Tolerance,
            "duration=0 ScreenTile must remain an instant event.");
        Require(ReadTile(instant) == (0.0, 1.0),
            "instant ScreenTile target was not preserved.");

        Require(ReadTile(identity) == (1.0, 1.0),
            "identity ScreenTile target was not preserved.");
        Require(!inactive.Active,
            "active=false ScreenTile occurrence was lost before runtime filtering.");
    }

    private static void RequireManagedRuntimeContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type builder = RequireType(assembly, "ExtremeEditor.Wpf.Native.ScreenTileTimelineBuilder");
        Type nativeEvent = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeScreenTileEvent");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeScreenTileState");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.ScreenTileStateEvaluator");

        Require(builder.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "ScreenTileTimelineBuilder.Build is missing.");

        foreach (string member in new[]
                 {
                     "StartTime",
                     "DurationSeconds",
                     "TargetX",
                     "TargetY",
                     "Ease",
                     "SourceIndex",
                     "Flags"
                 })
        {
            Require(HasMember(nativeEvent, member),
                $"NativeScreenTileEvent.{member} is missing.");
        }

        Require(HasStaticMember(nativeEvent, "FlagInstantPreservesExistingTween"),
            "ScreenTile duration=0 no-kill behavior has no explicit managed ABI flag.");
        Require(HasStaticMember(nativeEvent, "FlagEnableBeforeTween"),
            "ScreenTile non-(1,1) immediate-enable behavior has no explicit managed ABI flag.");
        Require(HasStaticMember(nativeEvent, "FlagDisableOnComplete"),
            "ScreenTile (1,1) completion-disable behavior has no explicit managed ABI flag.");
    }

    private static void RequireRendererContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type prepared = RequireType(assembly, "ExtremeEditor.Wpf.Native.PreparedNativePlayback");
        Require(prepared.GetProperty("ScreenTileTimeline") is not null,
            "PreparedNativePlayback.ScreenTileTimeline is missing.");

        Type session = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeRendererSession");
        Require(session.GetMethod(
            "SetScreenTileTimeline",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
            "NativeRendererSession.SetScreenTileTimeline is missing.");
    }

    private static void RequireNativeContract()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"));

        foreach (string token in new[]
                 {
                     "EeScreenTileEvent",
                     "ee_renderer_set_screen_tile_timeline",
                     "EE_SCREEN_TILE_INSTANT_PRESERVES_EXISTING_TWEEN",
                     "EE_SCREEN_TILE_ENABLE_BEFORE_TWEEN",
                     "EE_SCREEN_TILE_DISABLE_ON_COMPLETE"
                 })
        {
            Require(header.Contains(token, StringComparison.Ordinal),
                $"native ScreenTile ABI token '{token}' is missing.");
        }

        string sources = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "src"), "*.cpp")
            .Select(File.ReadAllText));

        Require(sources.Contains("ResolveScreenTileAtTime", StringComparison.Ordinal),
            "native ScreenTile deterministic evaluator is missing.");
        Require(sources.Contains("SetScreenTileState", StringComparison.Ordinal),
            "native renderer does not pass ScreenTile state into the rendering backend.");
    }

    private static (double X, double Y) ReadTile(VfxOccurrence occurrence)
    {
        JsonObject properties = occurrence.SourceEvent.PropertyOverrides ?? new JsonObject();
        if (properties["tile"] is not JsonArray { Count: >= 2 } tile)
            throw new InvalidOperationException("ScreenTile tile array is missing.");

        return (ReadDouble(tile[0]), ReadDouble(tile[1]));
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out double result))
            return result;
        throw new InvalidOperationException("ScreenTile tile coordinate is not numeric.");
    }

    private static bool HasMember(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static bool HasStaticMember(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
        type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is not null;

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
