using System.Collections;
using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportFoundationRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        TryVerify("VE0 playback parity", VerifySharedPlaybackVisualContract, failures);
        TryVerify("VE1 export timeline", VerifyExportTimelineContract, failures);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Video Export foundation is not available:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static void TryVerify(string label, Action verification, List<string> failures)
    {
        try
        {
            verification();
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message.Replace(Environment.NewLine, " | ")}");
        }
    }

    private static void VerifySharedPlaybackVisualContract()
    {
        string root = FindRepoRoot();
        string realtimePath = Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp");
        string headlessPath = Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer_headless.cpp");
        string sharedPath = Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "playback_visual.h");

        var missing = new List<string>();

        if (!File.Exists(sharedPath))
        {
            missing.Add("shared playback visual helper: playback_visual.h is missing");
        }
        else
        {
            string shared = File.ReadAllText(sharedPath);
            if (!shared.Contains("CalculatePlaybackVisual", StringComparison.Ordinal))
                missing.Add("shared playback visual helper: CalculatePlaybackVisual is missing");
            if (!shared.Contains("scene->floors[floor + 1", StringComparison.Ordinal) ||
                !shared.Contains("hypot", StringComparison.Ordinal))
            {
                missing.Add(
                    "ScaleRadius parity: shared playback visual must derive orbit radius from resolved current-to-next floor distance");
            }
        }

        string realtime = File.ReadAllText(realtimePath);
        string headless = File.ReadAllText(headlessPath);

        if (!realtime.Contains("#include \"playback_visual.h\"", StringComparison.Ordinal))
            missing.Add("realtime renderer does not use the shared playback visual helper");
        if (!headless.Contains("#include \"playback_visual.h\"", StringComparison.Ordinal))
            missing.Add("headless renderer does not use the shared playback visual helper");
        if (headless.Contains("CalculateHeadlessPlaybackVisual", StringComparison.Ordinal))
            missing.Add("headless renderer still owns a divergent playback visual implementation");
        if (headless.Contains("* PlanetDistance", StringComparison.Ordinal))
            missing.Add("headless renderer still uses fixed PlanetDistance and diverges on ScaleRadius charts");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "Video Export VE0 playback parity is not available:\n- " +
                string.Join("\n- ", missing));
        }
    }

    private static void VerifyExportTimelineContract()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type? planType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportPlan");
        Type? frameType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportFrame");
        Type? timelineType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportTimeline");

        var missing = new List<string>();
        if (planType is null)
            missing.Add("VideoExportPlan is missing");
        if (frameType is null)
            missing.Add("VideoExportFrame is missing");
        if (timelineType is null)
            missing.Add("VideoExportTimeline is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "Video Export VE1 timeline contract is not available:\n- " +
                string.Join("\n- ", missing));
        }

        object plan;
        try
        {
            plan = Activator.CreateInstance(
                       planType!,
                       [10.0, 15.0, 60, 1920, 1080])
                   ?? throw new InvalidOperationException("VideoExportPlan constructor returned null.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "VideoExportPlan must be constructible as " +
                "(startChartTimeSeconds, endChartTimeSeconds, framesPerSecond, width, height).",
                ex);
        }

        MethodInfo build = timelineType!.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [planType!],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VideoExportTimeline.Build(VideoExportPlan) is missing.");

        object built = build.Invoke(null, [plan])
            ?? throw new InvalidOperationException("VideoExportTimeline.Build returned null.");

        object[] frames = built is IEnumerable enumerable
            ? enumerable.Cast<object>().ToArray()
            : throw new InvalidOperationException(
                "VideoExportTimeline.Build must return an enumerable frame sequence.");

        if (frames.Length != 300)
        {
            throw new InvalidOperationException(
                $"A 5 second 60 FPS export must contain exactly 300 frames; got {frames.Length}.");
        }

        VerifyFrame(frames[0], frameType!, 0, 0.0, 10.0, 0.0);
        VerifyFrame(frames[1], frameType!, 1, 1.0 / 60.0, 10.0 + 1.0 / 60.0, 1.0 / 60.0);
        VerifyFrame(frames[^1], frameType!, 299, 299.0 / 60.0, 10.0 + 299.0 / 60.0, 299.0 / 60.0);

        double lastChartTime = ReadDouble(frames[^1], frameType!, "ChartTimeSeconds");
        if (!(lastChartTime < 15.0))
        {
            throw new InvalidOperationException(
                "Video export end time must be exclusive so the final frame never advances into the next range boundary.");
        }
    }

    private static void VerifyFrame(
        object frame,
        Type frameType,
        int expectedIndex,
        double expectedOutputTime,
        double expectedChartTime,
        double expectedVisualTime)
    {
        int index = ReadInt(frame, frameType, "Index");
        if (index != expectedIndex)
            throw new InvalidOperationException(
                $"Video export frame index mismatch: expected {expectedIndex}, got {index}.");

        Near(expectedOutputTime, ReadDouble(frame, frameType, "OutputTimeSeconds"), "output time");
        Near(expectedChartTime, ReadDouble(frame, frameType, "ChartTimeSeconds"), "chart time");
        Near(expectedVisualTime, ReadDouble(frame, frameType, "VisualTimeSeconds"), "visual time");
    }

    private static int ReadInt(object instance, Type type, string name)
    {
        PropertyInfo property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{type.Name}.{name} is missing.");
        object? value = property.GetValue(instance);
        return value is int result
            ? result
            : throw new InvalidOperationException($"{type.Name}.{name} must be an Int32.");
    }

    private static double ReadDouble(object instance, Type type, string name)
    {
        PropertyInfo property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{type.Name}.{name} is missing.");
        object? value = property.GetValue(instance);
        return value is double result
            ? result
            : throw new InvalidOperationException($"{type.Name}.{name} must be a Double.");
    }

    private static void Near(double expected, double actual, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-10)
        {
            throw new InvalidOperationException(
                $"Video export {label} mismatch: expected {expected:R}, got {actual:R}.");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.NativeRenderer")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for Video Export regression.");
    }
}
