using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class CustomBackgroundRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        string path = WriteFixture();
        try
        {
            LevelDocument level = AdoFaiLoader.Load(path).Document;
            TimingMap timing = TimingMapBuilder.Build(level);
            VfxTimeline timeline = VfxTimelineBuilder.Build(
                level,
                timing,
                static action => string.Equals(action.EventType, "CustomBackground", StringComparison.Ordinal));

            Check(failures, "stock root schema and defaults", () => RequireRootSchema(path));
            Check(failures, "CustomBackground occurrence semantics", () => RequireOccurrenceSemantics(level, timing, timeline));
            Check(failures, "round-trip and unsupported preservation", () => RequireRoundTrip(level));
            Check(failures, "managed background runtime contract", RequireManagedRuntimeContract);
            Check(failures, "renderer-facing background snapshot", RequireSnapshotContract);
            Check(failures, "native dedicated background layer", RequireNativeContract);
        }
        finally
        {
            File.Delete(path);
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "CustomBackground renderer integration is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static void RequireRootSchema(string path)
    {
        JsonObject settings = JsonNode.Parse(File.ReadAllText(path))!["settings"]!.AsObject();
        SpecState state = ReadRoot(settings);
        Require(state.Color == "102030", "root backgroundColor was not read.");
        Require(state.Image == "root-background.png", "root bgImage was not read.");
        Require(state.ImageColor == "11223344", "root bgImageColor was not read.");
        Require(state.ParallaxX == 80 && state.ParallaxY == 120, "root parallax was not read.");
        Require(state.DisplayMode == "FitToScreen", "root bgDisplayMode was not read.");
        Require(!state.ImageSmoothing && state.LockRotation && state.Loop && state.ScalingRatio == 125,
            "root smoothing/lock/loop/scaling values were not read.");

        SpecState defaults = ReadRoot(new JsonObject());
        Require(defaults.Color == "000000", "missing backgroundColor did not use stock default 000000.");
        Require(defaults.Image == string.Empty && defaults.ImageColor == "ffffff",
            "missing image settings did not use stock empty/white defaults.");
        Require(defaults.ParallaxX == 100 && defaults.ParallaxY == 100,
            "missing parallax did not use stock [100,100].");
        Require(defaults.DisplayMode == "FitToScreen" && defaults.ImageSmoothing &&
                !defaults.LockRotation && !defaults.Loop && defaults.ScalingRatio == 100,
            "missing root background settings did not use stock defaults.");
    }

    private static void RequireOccurrenceSemantics(
        LevelDocument level,
        TimingMap timing,
        VfxTimeline timeline)
    {
        VfxOccurrence[] occurrences = timeline.Occurrences
            .Where(static occurrence => occurrence.EventType == "CustomBackground")
            .ToArray();
        Require(occurrences.Any(static occurrence => occurrence.IsRepeated),
            "RepeatEvents did not produce a CustomBackground occurrence.");

        VfxOccurrence original = occurrences.First(static occurrence => occurrence.SourceIndex == 0);
        double expectedStart = timing.Floors[1].EntryTime + 0.5 * (60.0 / timing.Floors[1].Bpm);
        Require(Math.Abs(original.StartTime - expectedStart) < 0.000001,
            "angleOffset was not applied to CustomBackground trigger time.");

        SpecState root = ReadRoot(JsonNode.Parse(File.ReadAllText(level.SourcePath))!["settings"]!.AsObject());
        SpecState before = Evaluate(root, occurrences, original.StartTime - 0.0001);
        SpecState atOriginal = Evaluate(root, occurrences, original.StartTime);
        Require(before.Color == root.Color, "state before the first event did not reconstruct the root background.");
        Require(atOriginal.Color == "aa0000", "the original CustomBackground did not switch instantly.");
        Require(atOriginal.ImageColor == "445566", "event imageColor was confused with root bgImageColor.");

        VfxOccurrence repeated = occurrences.First(static occurrence => occurrence.IsRepeated);
        SpecState afterRepeat = Evaluate(root, occurrences, repeated.StartTime);
        Require(afterRepeat.Color == "aa0000", "repeated occurrence did not retain its source payload.");

        double sameTime = occurrences
            .Where(static occurrence => occurrence.SourceIndex is 3 or 4)
            .Select(static occurrence => occurrence.StartTime)
            .Distinct()
            .Single();
        SpecState sameTimeState = Evaluate(root, occurrences, sameTime);
        Require(sameTimeState.Color == "00cc00", "same-time source ordering did not make the later event authoritative.");

        SpecState rewind = Evaluate(root, occurrences, original.StartTime - 0.0001);
        Require(rewind == before, "backward seek did not deterministically reconstruct the root state.");
        Require(occurrences.Any(static occurrence => !occurrence.Active && occurrence.SourceIndex == 2),
            "inactive fixture occurrence is missing.");
        Require(Evaluate(root, occurrences, sameTime - 0.0001).Color != "ff00ff",
            "active:false CustomBackground changed effective state.");
    }

    private static void RequireRoundTrip(LevelDocument level)
    {
        var session = new EditorSession(level);
        JsonObject root = session.GetSourceRootForSave();
        JsonObject settings = root["settings"]!.AsObject();
        Require(settings["futureBackgroundSetting"]?["keep"]?.GetValue<bool>() == true,
            "unknown root background setting was lost.");
        JsonObject first = root["actions"]!.AsArray()[0]!.AsObject();
        Require(first["unscaledSize"]?.GetValue<bool>() == true,
            "unscaledSize was migrated or lost instead of being preserved as unknown data.");
        Require(first["duration"]?.GetValue<double>() == 9 && first["ease"]?.GetValue<string>() == "InSine",
            "unsupported duration/ease were removed from source JSON.");
        Require(first["imageColor"]?.GetValue<string>() == "445566" && first["bgImageColor"] is null,
            "event imageColor ownership was changed during round-trip.");
    }

    private static void RequireManagedRuntimeContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        RequireType(assembly, "ExtremeEditor.Wpf.Native.BackgroundSourceReader");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.BackgroundTimelineBuilder");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.BackgroundStateEvaluator");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeBackgroundState");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeBackgroundEvent");
        RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeBackgroundAsset");
    }

    private static void RequireSnapshotContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type snapshot = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeLevelSnapshot");
        foreach (string property in new[] { "BackgroundState", "BackgroundAssets" })
        {
            Require(snapshot.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
                $"NativeLevelSnapshot.{property} is missing.");
        }

        Type prepared = RequireType(assembly, "ExtremeEditor.Wpf.Native.PreparedNativePlayback");
        Require(prepared.GetProperty("BackgroundTimeline") is not null,
            "PreparedNativePlayback.BackgroundTimeline is missing.");

        Type session = RequireType(assembly, "ExtremeEditor.Wpf.Native.NativeRendererSession");
        foreach (string method in new[] { "SetBackgroundTimeline", "SetBackgroundAsset", "ClearBackgroundAssets" })
        {
            Require(session.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null,
                $"NativeRendererSession.{method} is missing.");
        }
    }

    private static void RequireNativeContract()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "include", "extreme_editor_renderer.h"));
        foreach (string token in new[]
                 {
                     "EeBackgroundState", "EeBackgroundEvent", "ee_renderer_set_background_timeline",
                     "ee_renderer_set_background_asset", "EE_BACKGROUND_DISPLAY_FIT_TO_SCREEN",
                     "EE_BACKGROUND_DISPLAY_UNSCALED", "EE_BACKGROUND_DISPLAY_TILED",
                     "EE_BACKGROUND_IMAGE_SMOOTHING", "EE_BACKGROUND_LOCK_ROTATION", "EE_BACKGROUND_LOOP"
                 })
        {
            Require(header.Contains(token, StringComparison.Ordinal), $"native background ABI token '{token}' is missing.");
        }

        string nativeSources = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "ExtremeEditor.NativeRenderer", "src"), "*.cpp")
            .Select(File.ReadAllText));
        foreach (string token in new[]
                 {
                     "BackgroundRuntime", "DrawBackground", "BackgroundAssetCache", "ResolveBackgroundAtTime"
                 })
        {
            Require(nativeSources.Contains(token, StringComparison.Ordinal),
                $"native dedicated/prepared background contract '{token}' is missing.");
        }
    }

    private static SpecState Evaluate(SpecState initial, IEnumerable<VfxOccurrence> occurrences, double chartTime)
    {
        SpecState state = initial;
        foreach (VfxOccurrence occurrence in occurrences
                     .Where(static occurrence => occurrence.EventType == "CustomBackground")
                     .OrderBy(static occurrence => occurrence.StartTime - occurrence.StartEffectOffset)
                     .ThenBy(static occurrence => occurrence.Floor))
        {
            if (!occurrence.Active || occurrence.StartTime > chartTime)
                continue;
            state = ApplyEvent(state, occurrence.SourceEvent.PropertyOverrides);
        }
        return state;
    }

    private static SpecState ReadRoot(JsonObject settings) => new(
        ReadString(settings, "backgroundColor", "000000"),
        ReadString(settings, "bgImage", string.Empty),
        ReadString(settings, "bgImageColor", "ffffff"),
        ReadPair(settings["parallax"], 100),
        ReadString(settings, "bgDisplayMode", "FitToScreen"),
        ReadBool(settings, "imageSmoothing", true),
        ReadBool(settings, "lockRot", false),
        ReadBool(settings, "loopBG", false),
        ReadDouble(settings, "scalingRatio", 100));

    private static SpecState ApplyEvent(SpecState previous, JsonObject? properties)
    {
        if (properties is null)
            return previous;
        (double x, double y) parallax = properties.ContainsKey("parallax")
            ? ReadPair(properties["parallax"], 100)
            : (previous.ParallaxX, previous.ParallaxY);
        return new SpecState(
            ReadString(properties, "color", previous.Color),
            ReadString(properties, "bgImage", previous.Image),
            ReadString(properties, "imageColor", previous.ImageColor),
            parallax,
            ReadString(properties, "bgDisplayMode", previous.DisplayMode),
            ReadBool(properties, "imageSmoothing", previous.ImageSmoothing),
            ReadBool(properties, "lockRot", previous.LockRotation),
            ReadBool(properties, "loopBG", previous.Loop),
            ReadDouble(properties, "scalingRatio", previous.ScalingRatio));
    }

    private static string ReadString(JsonObject source, string key, string fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out string? text) ? text ?? fallback : fallback;

    private static bool ReadBool(JsonObject source, string key, bool fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out bool result) ? result : fallback;

    private static double ReadDouble(JsonObject source, string key, double fallback) =>
        source[key] is JsonValue value && value.TryGetValue(out double result) ? result : fallback;

    private static (double X, double Y) ReadPair(JsonNode? node, double fallback)
    {
        if (node is not JsonArray { Count: >= 2 } array)
            return (fallback, fallback);
        return (array[0]?.GetValue<double>() ?? fallback, array[1]?.GetValue<double>() ?? fallback);
    }

    private static string WriteFixture()
    {
        var root = new JsonObject
        {
            ["angleData"] = new JsonArray(0, 90, 180, 270),
            ["settings"] = new JsonObject
            {
                ["bpm"] = 120,
                ["backgroundColor"] = "102030",
                ["bgImage"] = "root-background.png",
                ["bgImageColor"] = "11223344",
                ["parallax"] = new JsonArray(80, 120),
                ["bgDisplayMode"] = "FitToScreen",
                ["imageSmoothing"] = false,
                ["lockRot"] = true,
                ["loopBG"] = true,
                ["scalingRatio"] = 125,
                ["futureBackgroundSetting"] = new JsonObject { ["keep"] = true }
            },
            ["actions"] = new JsonArray(
                Background(1, true, 90, "aa0000", "bg", "445566", includeUnknowns: true),
                new JsonObject
                {
                    ["floor"] = 1, ["eventType"] = "RepeatEvents", ["active"] = true,
                    ["repeatType"] = "Beat", ["repetitions"] = 1, ["interval"] = 1, ["tag"] = "bg"
                },
                Background(1, false, 0, "ff00ff", null, "ffffff"),
                Background(2, true, 0, "0000cc", null, "ffffff"),
                Background(2, true, 0, "00cc00", null, "ffffff")),
            ["decorations"] = new JsonArray(),
            ["futureRoot"] = new JsonObject { ["keep"] = true }
        };
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-CustomBackground-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static JsonObject Background(
        int floor,
        bool active,
        double angleOffset,
        string color,
        string? eventTag,
        string imageColor,
        bool includeUnknowns = false)
    {
        var action = new JsonObject
        {
            ["floor"] = floor, ["eventType"] = "CustomBackground", ["active"] = active,
            ["color"] = color, ["bgImage"] = "event-background.png", ["imageColor"] = imageColor,
            ["parallax"] = new JsonArray(60, 70), ["bgDisplayMode"] = "Tiled",
            ["imageSmoothing"] = true, ["lockRot"] = false, ["loopBG"] = false,
            ["scalingRatio"] = 90, ["angleOffset"] = angleOffset
        };
        if (eventTag is not null)
            action["eventTag"] = eventTag;
        if (includeUnknowns)
        {
            action["unscaledSize"] = true;
            action["duration"] = 9;
            action["ease"] = "InSine";
        }
        return action;
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException($"production type '{name}' is missing.");

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

    private sealed record SpecState(
        string Color,
        string Image,
        string ImageColor,
        double ParallaxX,
        double ParallaxY,
        string DisplayMode,
        bool ImageSmoothing,
        bool LockRotation,
        bool Loop,
        double ScalingRatio)
    {
        public SpecState(
            string color,
            string image,
            string imageColor,
            (double X, double Y) parallax,
            string displayMode,
            bool imageSmoothing,
            bool lockRotation,
            bool loop,
            double scalingRatio)
            : this(color, image, imageColor, parallax.X, parallax.Y, displayMode,
                imageSmoothing, lockRotation, loop, scalingRatio)
        {
        }
    }
}
