using System.IO;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;

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
        VerifyCanceledSnapshotSkipsDisk();
        VerifyProgressLifecycle();
        VerifyDisposedCancellationSource();
        VerifyChartOutputPathGuard();
        VerifyServiceChartOutputPathGuard();
        VerifySubSampleRangeSkipsNativePreparation();
        VerifyOneSampleRangeReachesNativePreparation();
        VerifyQuotedFfmpegPathEntries();
        VerifyExpandedFfmpegPathEntries();
        VerifySourceLineEndingNormalization();
        VerifyUiContract();
        VerifyExclusiveUiSourceContract();
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

        int planIndex = source.IndexOf("var videoPlan = new VideoExportPlan(", StringComparison.Ordinal);
        int countIndex = source.IndexOf(
            "int totalVideoFrames = VideoExportTimeline.CountFrames(videoPlan);",
            StringComparison.Ordinal);
        int encoderIndex = source.IndexOf(
            "await using var encoder = new FfmpegVideoEncoder(", StringComparison.Ordinal);
        if (planIndex < 0 || countIndex <= planIndex || encoderIndex <= countIndex)
            failures.Add("service must validate the frame count before starting ffmpeg");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE5 orchestration source contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static void VerifyCanceledSnapshotSkipsDisk()
    {
        MethodInfo snapshot = typeof(MainWindow).GetMethod(
                "CreateVideoExportSnapshot", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Video export snapshot helper is missing.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string nonexistent = Path.Combine(Path.GetTempPath(),
            $"ExtremeEditor-CancelledSnapshot-{Guid.NewGuid():N}.adofai");
        try
        {
            snapshot.Invoke(null, [nonexistent, cancellation.Token]);
            throw new InvalidOperationException("A cancelled snapshot unexpectedly loaded a chart.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Cancellation must win before the first disk read.
        }
    }

    private static void VerifyProgressLifecycle()
    {
        MethodInfo gate = typeof(MainWindow).GetMethod(
                "ShouldReportVideoExportProgress",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Video export progress guard is missing.");

        using var cancellation = new CancellationTokenSource();
        bool ShouldReport(bool active) =>
            (bool)(gate.Invoke(null, [active, cancellation.Token])
                ?? throw new InvalidOperationException("Progress guard returned null."));

        if (!ShouldReport(true) || ShouldReport(false))
            throw new InvalidOperationException(
                "Video export progress must be reported only while the export is active.");

        cancellation.Cancel();
        if (ShouldReport(true))
            throw new InvalidOperationException(
                "Video export progress must not overwrite cancellation status.");
    }

    private static void VerifyDisposedCancellationSource()
    {
        MethodInfo cancel = typeof(MainWindow).GetMethod(
                "TryCancelVideoExport", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Video export cancellation helper is missing.");

        bool TryCancel(CancellationTokenSource? source) =>
            (bool)(cancel.Invoke(null, [source])
                ?? throw new InvalidOperationException("Cancellation helper returned null."));

        if (TryCancel(null))
            throw new InvalidOperationException("A missing cancellation source must be ignored.");

        using var source = new CancellationTokenSource();
        if (!TryCancel(source) || !source.IsCancellationRequested)
            throw new InvalidOperationException("An active export must accept cancellation.");

        source.Dispose();
        if (TryCancel(source))
            throw new InvalidOperationException(
                "A disposed cancellation source must be ignored without throwing.");
    }

    private static void VerifyChartOutputPathGuard()
    {
        MethodInfo guard = typeof(MainWindow).GetMethod(
                "IsVideoExportOutputSameAsChart", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Video export chart overwrite guard is missing.");

        bool Collides(string output, string chart) =>
            (bool)(guard.Invoke(null, [output, chart])
                ?? throw new InvalidOperationException("Chart overwrite guard returned null."));

        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor-VideoPathGuard");
        string chart = Path.Combine(directory, "chart.adofai");
        string normalizedAlias = Path.Combine(directory, "sub", "..", "chart.adofai");
        string video = Path.Combine(directory, "chart.mp4");
        if (!Collides(chart, chart) ||
            !Collides(normalizedAlias, chart) ||
            !Collides(chart.ToUpperInvariant(), chart) ||
            Collides(video, chart))
        {
            throw new InvalidOperationException(
                "Video export must reject chart path aliases without rejecting a distinct MP4.");
        }

        string uiSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.VideoExport.cs"));
        int accepted = uiSource.IndexOf("if (saveDialog.ShowDialog(this) != true)", StringComparison.Ordinal);
        int checkedPath = uiSource.IndexOf(
            "if (IsVideoExportOutputSameAsChart(saveDialog.FileName, chartPath))", StringComparison.Ordinal);
        int snapshot = uiSource.IndexOf(
            "VideoExportSource source = await Task.Run(", StringComparison.Ordinal);
        if (accepted < 0 || checkedPath <= accepted || snapshot <= checkedPath)
            throw new InvalidOperationException(
                "Video export must reject source-chart output paths before snapshot preparation.");
    }

    private static void VerifyServiceChartOutputPathGuard()
    {
        // The service is callable without MainWindow's save-dialog check.
        // Its preflight must not let an MP4 replace the loaded .adofai.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-ServiceVideoGuard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string chart = Path.Combine(directory, "chart.adofai");
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            File.WriteAllText(chart, "source chart sentinel");
            File.WriteAllText(ffmpeg, "ffmpeg placeholder");

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            level.SourcePath = chart;
            var source = new VideoExportSource(
                level, TimingMapBuilder.Build(level), HitSoundTimelineBuilder.Build(level), null);

            VideoExportRequest Request(string output) => new(
                source, output, ffmpeg, 0.0, 1.0, 30, 2, 2, 100.0, false);

            foreach (string output in new[]
            {
                chart,
                Path.Combine(directory, "sub", "..", "chart.adofai"),
                chart.ToUpperInvariant()
            })
            {
                bool rejected = false;
                try
                {
                    Request(output).Validate();
                }
                catch (ArgumentException ex)
                    when (ex.ParamName == nameof(VideoExportRequest.OutputPath))
                {
                    rejected = true;
                }

                if (!rejected)
                    throw new InvalidOperationException(
                        "Service preflight must reject a source-chart output path alias.");
            }

            if (File.ReadAllText(chart) != "source chart sentinel")
                throw new InvalidOperationException("Rejected export modified the source chart.");

            Request(Path.Combine(directory, "chart.mp4")).Validate();

            // Synthetic documents have no on-disk chart to protect.
            level.SourcePath = "<synthetic>";
            Request(chart).Validate();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySubSampleRangeSkipsNativePreparation()
    {
        // A valid positive chart interval can round to zero PCM frames.
        // It must fail before loading native assets or opening ffmpeg staging.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-SubSampleVideo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            string output = Path.Combine(directory, "subsample.mp4");
            File.WriteAllText(ffmpeg, "ffmpeg placeholder");

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            var source = new VideoExportSource(
                level, TimingMapBuilder.Build(level), HitSoundTimelineBuilder.Build(level), null);
            var request = new VideoExportRequest(
                source, output, ffmpeg, 0.0, 0.000001, 60, 2, 2, 100.0, false);
            int nativeFactoryCalls = 0;
            var service = new VideoExportService((_, _) =>
            {
                nativeFactoryCalls++;
                throw new InvalidOperationException("Native renderer must not be initialized.");
            });

            bool rejected = false;
            try
            {
                service.ExportAsync(
                    request,
                    new Progress<VideoExportProgress>(_ => { }),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("shorter than one PCM sample", StringComparison.Ordinal))
            {
                rejected = true;
            }

            if (!rejected || nativeFactoryCalls != 0 || File.Exists(output))
                throw new InvalidOperationException(
                    "Sub-sample video export must fail before native or output initialization.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyOneSampleRangeReachesNativePreparation()
    {
        // The zero-sample guard must not reject a valid one-sample interval.
        // Stop at the native factory so no real renderer or ffmpeg is needed.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-OneSampleVideo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            string output = Path.Combine(directory, "one-sample.mp4");
            File.WriteAllText(ffmpeg, "ffmpeg placeholder");

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            var source = new VideoExportSource(
                level, TimingMapBuilder.Build(level), HitSoundTimelineBuilder.Build(level), null);
            var request = new VideoExportRequest(
                source, output, ffmpeg, 0.0, 1.0 / 44100.0, 60, 2, 2, 100.0, false);
            int nativeFactoryCalls = 0;
            var service = new VideoExportService((_, _) =>
            {
                nativeFactoryCalls++;
                throw new InvalidOperationException("One-sample native factory sentinel.");
            });

            bool reachedNative = false;
            try
            {
                service.ExportAsync(
                    request,
                    new Progress<VideoExportProgress>(_ => { }),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
                when (ex.Message == "One-sample native factory sentinel.")
            {
                reachedNative = true;
            }

            if (!reachedNative || nativeFactoryCalls != 1 || File.Exists(output))
                throw new InvalidOperationException(
                    "A one-sample video export must pass audio preflight and reach native preparation.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyQuotedFfmpegPathEntries()
    {
        MethodInfo resolver = typeof(MainWindow).GetMethod(
                "FindFfmpegInPathEntries", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ffmpeg PATH entry resolver is missing.");

        string? Resolve(string entries) => resolver.Invoke(null, [entries]) as string;
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-QuotedFfmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "ffmpeg.exe");
            File.WriteAllText(executable, "test executable placeholder");
            string quoted = "\"" + directory + "\"";
            string missing = Path.Combine(directory, "not-a-directory");
            string expected = Path.GetFullPath(executable);

            if (!string.Equals(Resolve(quoted), expected, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Resolve(missing + Path.PathSeparator + quoted),
                    expected, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Resolve(directory), expected, StringComparison.OrdinalIgnoreCase) ||
                Resolve(missing) is not null)
            {
                throw new InvalidOperationException(
                    "ffmpeg lookup must handle quoted/unquoted PATH entries and skip missing entries.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyExpandedFfmpegPathEntries()
    {
        // Windows PATH can contain unexpanded %VARIABLE% references. The
        // lookup must resolve them whether or not the entry is quoted.
        MethodInfo resolver = typeof(MainWindow).GetMethod(
                "FindFfmpegInPathEntries", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ffmpeg PATH entry resolver is missing.");

        string root = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-FfmpegEnv-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "path with spaces");
        string variable = $"EXTREMEEDITOR_FFMPEG_PATH_{Guid.NewGuid():N}";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Directory.CreateDirectory(directory);
        try
        {
            string expected = Path.GetFullPath(Path.Combine(directory, "ffmpeg.exe"));
            File.WriteAllText(expected, "test executable placeholder");
            Environment.SetEnvironmentVariable(variable, directory);
            string reference = $"%{variable}%";
            foreach (string entry in new[] { reference, $"\\"{reference}\\"" })
            {
                string? actual = resolver.Invoke(null, [entry]) as string;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "ffmpeg lookup must expand environment variables in quoted and unquoted PATH entries.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    // Source-contract checks must behave identically for LF and Windows CRLF
    // checkouts. Do not compare raw platform-specific newline sequences.
    private static string NormalizeSourceLineEndings(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void VerifySourceLineEndingNormalization()
    {
        const string windowsSource = "finally\r\n        {\r\n            progressActive = false;";
        const string expected = "finally\n        {\n            progressActive = false;";
        if (!string.Equals(NormalizeSourceLineEndings(windowsSource), expected, StringComparison.Ordinal) ||
            !string.Equals(NormalizeSourceLineEndings(expected), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Video export source contracts must be independent of checkout line endings.");
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
        if (!xaml.Contains("CancelVideoExportMenuItem", StringComparison.Ordinal) ||
            !xaml.Contains("CancelVideoExportClick", StringComparison.Ordinal))
            failures.Add("File menu must expose a keyboard-reachable cancel action during export");
        if (!File.Exists(codePath))
            failures.Add("MainWindow.VideoExport.cs is missing");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE5 UI contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    // This checks the actual event-handler control flow without starting WPF dialogs
    // or FFmpeg. Windows GUI concurrency and exception presentation still require
    // separate integration testing.
    private static void VerifyExclusiveUiSourceContract()
    {
        string path = Path.Combine(
            FindRepoRoot(), "src", "ExtremeEditor.Wpf", "MainWindow",
            "MainWindow.VideoExport.cs");
        string source = NormalizeSourceLineEndings(File.ReadAllText(path));

        int handlerStart = source.IndexOf("private async void ExportVideoClick(", StringComparison.Ordinal);
        int coreStart = source.IndexOf("private async Task ExportVideoCoreAsync(", StringComparison.Ordinal);
        int nextMember = source.IndexOf("internal static bool RequiresSaveBeforeVideoExport(", StringComparison.Ordinal);
        if (handlerStart < 0 || coreStart <= handlerStart || nextMember <= coreStart)
            throw new InvalidOperationException("Export Video must have a guarded async handler and a preparation core.");

        string handler = source[handlerStart..coreStart];
        string core = source[coreStart..nextMember];
        int guard = handler.IndexOf("_videoExportInProgress = true;", StringComparison.Ordinal);
        int disable = handler.IndexOf("ExportVideoMenuItem.IsEnabled = false;", StringComparison.Ordinal);
        int tryStart = handler.IndexOf("        try", StringComparison.Ordinal);
        int awaitCore = handler.IndexOf("await ExportVideoCoreAsync();", StringComparison.Ordinal);
        int catchFailure = handler.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        int cleanup = handler.IndexOf("finally", StringComparison.Ordinal);
        int release = handler.IndexOf("_videoExportInProgress = false;", StringComparison.Ordinal);

        if (!handler.Contains("if (_videoExportInProgress ||", StringComparison.Ordinal) ||
            guard < 0 || disable < guard || tryStart < disable || awaitCore < tryStart ||
            catchFailure < awaitCore || cleanup < catchFailure || release < cleanup ||
            !handler.Contains("_videoExportCancellation = null;", StringComparison.Ordinal) ||
            !handler.Contains("ExportVideoMenuItem.IsEnabled = true;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Export Video must acquire the single-flight guard before any await and release it in finally.");
        }

        if (!core.Contains("await SaveCurrentAsync(", StringComparison.Ordinal) ||
            !core.Contains("CreateVideoExportSnapshot(chartPath, cancellation.Token)", StringComparison.Ordinal) ||
            !core.Contains("await service.ExportAsync(", StringComparison.Ordinal) ||
            core.Contains("_videoExportInProgress = true;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Save, snapshot preparation, and encoding must all execute inside the guarded core.");
        }

        int cancelSetup = core.IndexOf("using var cancellation = new CancellationTokenSource();", StringComparison.Ordinal);
        int enableCancel = core.IndexOf("CancelVideoExportMenuItem.IsEnabled = true;", StringComparison.Ordinal);
        int snapshotStart = core.IndexOf("VideoExportSource source = await Task.Run(", StringComparison.Ordinal);
        int snapshotToken = core.IndexOf("CreateVideoExportSnapshot(chartPath, cancellation.Token)", StringComparison.Ordinal);
        int taskToken = core.IndexOf("            cancellation.Token);", StringComparison.Ordinal);
        int capturedHitSounds = core.IndexOf("bool hitSoundsEnabled = HitSoundsToggle.IsChecked == true;", StringComparison.Ordinal);
        if (cancelSetup < 0 || enableCancel <= cancelSetup ||
            snapshotStart <= enableCancel || snapshotToken <= snapshotStart ||
            taskToken <= snapshotToken || capturedHitSounds <= enableCancel ||
            capturedHitSounds >= snapshotStart ||
            !core.Contains("HitSoundsEnabled: hitSoundsEnabled", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cancellation and export options must be available before asynchronous snapshot preparation.");
        }

        int progressGuard = core.IndexOf(
            "if (!ShouldReportVideoExportProgress(progressActive, progressToken))",
            StringComparison.Ordinal);
        int progressStatus = core.IndexOf(
            "StatusText.Text = $" + "\"Export Video — {value.Stage}{suffix}\";",
            StringComparison.Ordinal);
        int progressDisable = core.IndexOf("progressActive = false;", StringComparison.Ordinal);
        int exportAwait = core.IndexOf("await service.ExportAsync(", StringComparison.Ordinal);
        if (!core.Contains("bool progressActive = true;", StringComparison.Ordinal) ||
            !core.Contains("CancellationToken progressToken = cancellation.Token;", StringComparison.Ordinal) ||
            progressGuard < 0 || progressStatus <= progressGuard ||
            progressDisable <= exportAwait ||
            !core.Contains("finally\n        {\n            progressActive = false;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Late video export progress must be ignored after completion or cancellation.");
        }

        if (!source.Contains("private void CancelVideoExportClick(", StringComparison.Ordinal) ||
            !source.Contains("TryCancelVideoExport(cancellation)", StringComparison.Ordinal) ||
            !source.Contains("CancelVideoExportMenuItem.IsEnabled = false;", StringComparison.Ordinal) ||
            !core.Contains("CancelVideoExportMenuItem.IsEnabled = true;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Video export must expose cancellation only while a cancellable operation is active.");
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
