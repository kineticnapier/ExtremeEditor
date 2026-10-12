using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportOrchestrationRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;

        Type? requestType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportRequest");
        Type? progressType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportProgress");
        Type? serviceType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportService");

        var missing = new List<string>();
        if (requestType is null)
            missing.Add("VideoExportRequest is missing");
        if (progressType is null)
            missing.Add("VideoExportProgress is missing");
        if (serviceType is null)
            missing.Add("VideoExportService is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "VE5 orchestration is not available:\n- " +
                string.Join("\n- ", missing));
        }

        VerifyServiceApi(serviceType!, requestType!, progressType!);
        VerifyServiceSourceContract();
        VerifyUiContract();
    }

    private static void VerifyServiceApi(
        Type serviceType,
        Type requestType,
        Type progressType)
    {
        Type progressContract = typeof(IProgress<>).MakeGenericType(progressType);

        MethodInfo? exportAsync = serviceType.GetMethod(
            "ExportAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [requestType, progressContract, typeof(CancellationToken)],
            modifiers: null);

        if (exportAsync is null || exportAsync.ReturnType != typeof(ValueTask))
        {
            throw new InvalidOperationException(
                "VideoExportService.ExportAsync(VideoExportRequest, IProgress<VideoExportProgress>, CancellationToken) is missing.");
        }
    }

    private static void VerifyServiceSourceContract()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportService.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException("VE5 VideoExportService source is missing.");

        string source = File.ReadAllText(path);
        var failures = new List<string>();

        if (!source.Contains("NativeChartFrameRenderer", StringComparison.Ordinal))
            failures.Add("service must render video through the native headless renderer");
        if (!source.Contains("VideoExportFramePipeline", StringComparison.Ordinal))
            failures.Add("service must use the VE2 frame pipeline");
        if (!source.Contains("OfflineAudioRenderer", StringComparison.Ordinal))
            failures.Add("service must use the VE3 offline audio renderer");
        if (!source.Contains("VideoExportMuxPipeline", StringComparison.Ordinal))
            failures.Add("service must use the VE4 mux pipeline");
        if (!source.Contains("FfmpegVideoEncoder", StringComparison.Ordinal))
            failures.Add("service must use the ffmpeg MP4 backend");
        if (!source.Contains("PlaybackClock.ChartToAudioTime", StringComparison.Ordinal))
            failures.Add("service must map chart range boundaries into audio time");
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("service must observe cancellation");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE5 orchestration source contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static void VerifyUiContract()
    {
        string root = FindRepoRoot();
        string xamlPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml");
        string codePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.VideoExport.cs");

        string xaml = File.ReadAllText(xamlPath);
        var failures = new List<string>();

        if (!xaml.Contains("Export _Video", StringComparison.Ordinal) &&
            !xaml.Contains("Export Video", StringComparison.Ordinal))
        {
            failures.Add("File menu must expose Export Video");
        }
        if (!xaml.Contains("ExportVideoClick", StringComparison.Ordinal))
            failures.Add("Export Video menu item must be wired to ExportVideoClick");
        if (!File.Exists(codePath))
            failures.Add("MainWindow.VideoExport.cs is missing");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE5 UI contract failed:\n- " +
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
            "Could not locate the ExtremeEditor repository root for VE5 regression.");
    }
}
