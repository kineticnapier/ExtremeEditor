using System.IO;
using System.Reflection;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportTrackParityRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type? rendererType = assembly.GetType("ExtremeEditor.Wpf.Native.NativeChartFrameRenderer");
        if (rendererType is null)
            throw new InvalidOperationException("NativeChartFrameRenderer is missing.");

        MethodInfo? loadPrepared = rendererType.GetMethod(
            "Load",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(LevelDocument), typeof(TimingMap)],
            modifiers: null);

        if (loadPrepared is null)
        {
            throw new InvalidOperationException(
                "Video export renderer must accept the already metadata-attached LevelDocument and TimingMap.");
        }

        VerifySourceContract();
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string servicePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportService.cs");
        string rendererPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Native", "NativeChartFrameRenderer.cs");

        string service = File.ReadAllText(servicePath);
        string renderer = File.ReadAllText(rendererPath);
        var failures = new List<string>();

        if (!service.Contains("WpfLevelLoader.Load", StringComparison.Ordinal))
            failures.Add("video export must use WpfLevelLoader so track visual/transform/ScaleRadius metadata caches are attached");
        if (service.Contains("AdoFaiLoader.Load(", StringComparison.Ordinal))
            failures.Add("video export must not bypass WpfLevelLoader with the core-only AdoFaiLoader");
        if (!service.Contains("frameRenderer.Load(level, timingMap)", StringComparison.Ordinal))
            failures.Add("headless renderer must receive the same metadata-attached document used by export orchestration");
        if (!renderer.Contains("Load(LevelDocument level, TimingMap timingMap)", StringComparison.Ordinal))
            failures.Add("NativeChartFrameRenderer must expose a prepared-document load path");
        if (renderer.Contains("AdoFaiLoader.Load(fullPath)", StringComparison.Ordinal))
            failures.Add("NativeChartFrameRenderer string load path must not lose renderer-specific metadata");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Video export track parity contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Wpf")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for video export track parity regression.");
    }
}
