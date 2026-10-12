using System.IO;
using System.Reflection;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportTrackParityRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;

        Type? sourceType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportSource");
        Type? requestType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportRequest");
        Type? serviceType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportService");
        Type? rendererType = assembly.GetType("ExtremeEditor.Wpf.Native.NativeChartFrameRenderer");

        var missing = new List<string>();
        if (sourceType is null)
            missing.Add("VideoExportSource is missing");
        if (requestType is null)
            missing.Add("VideoExportRequest is missing");
        if (serviceType is null)
            missing.Add("VideoExportService is missing");
        if (rendererType is null)
            missing.Add("NativeChartFrameRenderer is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "Video export shared-source contract is not available:\n- " +
                string.Join("\n- ", missing));
        }

        VerifyPreparedSourceContract(sourceType!, requestType!);
        VerifyRendererContract(rendererType!);
        VerifySourceContract();
    }

    private static void VerifyPreparedSourceContract(Type sourceType, Type requestType)
    {
        foreach ((string name, Type type) in new[]
        {
            ("Level", typeof(LevelDocument)),
            ("TimingMap", typeof(TimingMap)),
            ("HitSoundTimeline", typeof(HitSoundTimeline)),
            ("SongPath", typeof(string))
        })
        {
            PropertyInfo? property = sourceType.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property is null || property.PropertyType != type)
            {
                throw new InvalidOperationException(
                    $"VideoExportSource.{name} must preserve the already-prepared editor source.");
            }
        }

        PropertyInfo? sourceProperty = requestType.GetProperty(
            "Source",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (sourceProperty is null || sourceProperty.PropertyType != sourceType)
        {
            throw new InvalidOperationException(
                "VideoExportRequest must carry VideoExportSource instead of asking the service to reload the chart.");
        }
    }

    private static void VerifyRendererContract(Type rendererType)
    {
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
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string servicePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportService.cs");
        string uiPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.VideoExport.cs");
        string rendererPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Native", "NativeChartFrameRenderer.cs");

        string service = File.ReadAllText(servicePath);
        string ui = File.ReadAllText(uiPath);
        string renderer = File.ReadAllText(rendererPath);
        var failures = new List<string>();

        if (service.Contains("AdoFaiLoader.Load", StringComparison.Ordinal) ||
            service.Contains("WpfLevelLoader.Load", StringComparison.Ordinal))
        {
            failures.Add("VideoExportService must not reload or reinterpret the chart at all");
        }
        if (!service.Contains("request.Source", StringComparison.Ordinal))
            failures.Add("VideoExportService must consume the prepared VideoExportSource from the request");
        if (!service.Contains("frameRenderer.Load(level, timingMap)", StringComparison.Ordinal))
            failures.Add("headless renderer must receive the same prepared LevelDocument and TimingMap");
        if (!renderer.Contains("Load(LevelDocument level, TimingMap timingMap)", StringComparison.Ordinal))
            failures.Add("NativeChartFrameRenderer must expose a prepared-document load path");

        if (!ui.Contains("new VideoExportSource(", StringComparison.Ordinal))
            failures.Add("MainWindow must package the currently loaded editor document/timelines into VideoExportSource");
        if (!ui.Contains("_level", StringComparison.Ordinal) ||
            !ui.Contains("_timingMap", StringComparison.Ordinal) ||
            !ui.Contains("_hitSoundTimeline", StringComparison.Ordinal))
        {
            failures.Add("MainWindow export must reuse the current editor level, timing map, and hitsound timeline");
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Video export shared-source contract failed:\n- " +
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
