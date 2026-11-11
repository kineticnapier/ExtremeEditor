using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Export;

// Win32 aliases trailing dots/spaces and reserves device names such as
// CON.mp4 and LPT1.txt even with an extension. Reject these destinations
// before comparing protected paths or creating encoder staging files.
internal static class VideoExportOutputPathGuard
{
    internal static bool HasAmbiguousWindowsComponent(string path)
    {
        string[] components = path.Split(
            ['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            if (component is "." or "..")
                continue;
            // A colon outside a drive designator denotes an NTFS alternate
            // data stream (or an invalid Win32 path). Never export into one.
            // Preserve C:\\ and C:relative.mp4 drive syntax, including the
            // drive component following an extended Win32 path prefix.
            int colon = component.IndexOf(':');
            bool isDrivePosition = index == 0 ||
                (index == 1 &&
                 (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                  path.StartsWith(@"\\.\", StringComparison.Ordinal)));
            bool driveDesignator = isDrivePosition && colon == 1 &&
                char.IsAsciiLetter(component[0]) &&
                component.IndexOf(':', 2) < 0;
            if ((colon >= 0 && !driveDesignator) ||
                component.EndsWith('.') || component.EndsWith(' ') ||
                IsReservedWindowsDeviceComponent(component))
                return true;
        }

        return false;
    }

    private static bool IsReservedWindowsDeviceComponent(string component)
    {
        // Win32 device names remain reserved when followed by an extension.
        // The superscript digits are also recognized by Windows for COM/LPT.
        int dot = component.IndexOf('.');
        string stem = (dot >= 0 ? component[..dot] : component)
            .TrimEnd(' ')
            .ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            return true;
        if (stem.Length != 4 ||
            (!stem.StartsWith("COM", StringComparison.Ordinal) &&
             !stem.StartsWith("LPT", StringComparison.Ordinal)))
            return false;
        char port = stem[3];
        return (port >= '1' && port <= '9') || port is '¹' or '²' or '³';
    }
}

internal sealed record VideoExportSource(
    LevelDocument Level,
    TimingMap TimingMap,
    HitSoundTimeline HitSoundTimeline,
    string? SongPath)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Level);
        ArgumentNullException.ThrowIfNull(TimingMap);
        ArgumentNullException.ThrowIfNull(HitSoundTimeline);
        // A directory is not a song. File.Exists would treat it as missing,
        // silently producing a video without the requested music.
        // Preserve the intentional silent fallback for null/missing files.
        if (!string.IsNullOrWhiteSpace(SongPath) &&
            Directory.Exists(Path.GetFullPath(SongPath)))
            throw new ArgumentException(
                "Video export song path must name a file, not a directory.",
                nameof(SongPath));
    }
}

internal sealed record VideoExportRequest(
    VideoExportSource Source,
    string OutputPath,
    string FfmpegPath,
    double StartChartTimeSeconds,
    double EndChartTimeSeconds,
    int FramesPerSecond,
    int Width,
    int Height,
    double SongVolumePercent,
    bool HitSoundsEnabled)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        Source.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (VideoExportOutputPathGuard.HasAmbiguousWindowsComponent(OutputPath))
            throw new ArgumentException(
                "Video output path contains an ambiguous or reserved Windows name.",
                nameof(OutputPath));
        // Reject malformed destinations before probing audio or loading native
        // scenes. FfmpegVideoEncoder.BeginAsync also resolves this path, but
        // doing so there is too late for an expensive export preflight.
        string resolvedOutputPath = Path.GetFullPath(OutputPath);
        // A directory cannot be replaced with an encoded MP4. Reject it
        // before audio probing and expensive native scene preparation.
        if (Path.EndsInDirectorySeparator(OutputPath) || Directory.Exists(resolvedOutputPath))
            throw new ArgumentException("Video output path must name a file, not a directory.", nameof(OutputPath));
        // An existing read-only destination cannot be safely replaced at the
        // end of an otherwise expensive export. Reject it before probing audio
        // or initializing the native renderer.
        if (File.Exists(resolvedOutputPath) &&
            (File.GetAttributes(resolvedOutputPath) & FileAttributes.ReadOnly) != 0)
            throw new ArgumentException(
                "Video output file is read-only.", nameof(OutputPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        if (!File.Exists(FfmpegPath))
            throw new FileNotFoundException("ffmpeg executable was not found.", FfmpegPath);
        // The UI checks the source chart, but direct service callers can bypass
        // the save dialog. Never replace the chart, source audio or ffmpeg itself.
        string? chartPath = Source.Level.SourcePath;
        if ((!string.IsNullOrWhiteSpace(chartPath) &&
             !string.Equals(chartPath, "<synthetic>", StringComparison.Ordinal) &&
             string.Equals(resolvedOutputPath, Path.GetFullPath(chartPath),
                 StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(resolvedOutputPath, Path.GetFullPath(FfmpegPath),
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(Source.SongPath) &&
             string.Equals(resolvedOutputPath, Path.GetFullPath(Source.SongPath),
                 StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "Video output path must differ from the source chart, song, and ffmpeg executable paths.",
                nameof(OutputPath));
        if (!double.IsFinite(StartChartTimeSeconds) || StartChartTimeSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(StartChartTimeSeconds));
        if (!double.IsFinite(EndChartTimeSeconds) ||
            EndChartTimeSeconds <= StartChartTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(EndChartTimeSeconds));
        if (FramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Height));
        // Reject dimensions that the configured libx264/yuv420p output cannot encode.
        if ((Width & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "H.264 yuv420p requires an even width.");
        if ((Height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(Height), "H.264 yuv420p requires an even height.");
        _ = VideoExportRgb24Buffer.GetRequiredByteCount(Width, Height);
        // Reject non-finite volume before opening output staging files.
        // OfflineAudioRenderPlan.Validate also enforces this downstream.
        if (!double.IsFinite(SongVolumePercent))
            throw new ArgumentOutOfRangeException(nameof(SongVolumePercent));
    }
}

internal sealed record VideoExportProgress(
    string Stage,
    int Completed,
    int Total)
{
    internal double Fraction =>
        Total <= 0 ? 0.0 : Math.Clamp((double)Completed / Total, 0.0, 1.0);
}

internal sealed class VideoExportService
{
    private readonly Func<int, int, NativeChartFrameRenderer> _rendererFactory;

    internal VideoExportService(Func<int, int, NativeChartFrameRenderer>? rendererFactory = null)
    {
        _rendererFactory = rendererFactory ??
            ((width, height) => new NativeChartFrameRenderer(width, height));
    }

    internal async ValueTask ExportAsync(
        VideoExportRequest request,
        IProgress<VideoExportProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        VideoExportSource source = request.Source;
        LevelDocument level = source.Level;
        TimingMap timingMap = source.TimingMap;
        HitSoundTimeline hitSoundTimeline = source.HitSoundTimeline;

        double startChart = Math.Clamp(
            request.StartChartTimeSeconds,
            0.0,
            timingMap.Duration);
        double endChart = Math.Clamp(
            request.EndChartTimeSeconds,
            0.0,
            timingMap.Duration);
        if (endChart <= startChart)
            throw new InvalidOperationException("Video export chart range is empty.");

        double startAudio = PlaybackClock.ChartToAudioTime(level, startChart);
        double endAudio = PlaybackClock.ChartToAudioTime(level, endChart);
        if (endAudio <= startAudio)
            throw new InvalidOperationException("Video export audio range is empty.");

        // Validate the complete frame count before ffmpeg or audio output starts.
        var videoPlan = new VideoExportPlan(
            startChart,
            endChart,
            request.FramesPerSecond,
            request.Width,
            request.Height)
        {
            ChartRate = Math.Max(0.000001, level.PitchPercent * 0.01)
        };
        int totalVideoFrames = VideoExportTimeline.CountFrames(videoPlan);

        string? songPath = source.SongPath;
        // Validate mapped audio times before probing the song or creating
        // the encoder's temporary files and output directory.
        var audioPlan = new OfflineAudioRenderPlan(
            level,
            timingMap,
            hitSoundTimeline,
            songPath,
            startAudio,
            endAudio,
            request.SongVolumePercent,
            request.HitSoundsEnabled);
        audioPlan.Validate();

        // Probe the source before expensive native initialization. A malformed
        // audio file must not load the native scene or create output staging.
        // Keep audio probing off the WPF UI thread as well.
        var offlineAudio = new OfflineAudioRenderer();
        OfflineAudioFormat audioFormat = await Task.Run(
            () => offlineAudio.ProbeFormat(songPath),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The audio renderer returns no chunks when both boundaries round to
        // the same PCM frame. Reject that range before native scene loading,
        // ffmpeg staging, and an eventual empty-audio MP4 attempt.
        var (startSampleFrame, endSampleFrame) =
            audioPlan.GetSampleRange(audioFormat.SampleRate);
        if (endSampleFrame <= startSampleFrame)
            throw new InvalidOperationException(
                "Video export audio range is shorter than one PCM sample.");

        // Prepare the native scene before opening encoder staging files or
        // rendering audio. Keep expensive native loading off the WPF UI thread.
        // A failed load must dispose the partially initialized renderer.
        using var frameRenderer = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var renderer = _rendererFactory(request.Width, request.Height)
                ?? throw new InvalidOperationException(
                    "Native frame renderer factory returned null.");
            try
            {
                // Cancellation can arrive while the factory creates a renderer
                // or while native scene loading is in progress. Avoid doing
                // additional native work and dispose the partial scene.
                cancellationToken.ThrowIfCancellationRequested();
                renderer.Load(level, timingMap);
                cancellationToken.ThrowIfCancellationRequested();
                renderer.SetCleanView(true);
                cancellationToken.ThrowIfCancellationRequested();
                return renderer;
            }
            catch
            {
                renderer.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        await using var encoder = new FfmpegVideoEncoder(request.FfmpegPath);
        var mux = new VideoExportMuxPipeline(encoder);
        await mux.BeginAsync(
            new VideoEncoderStartInfo(
                request.OutputPath,
                request.Width,
                request.Height,
                request.FramesPerSecond,
                audioFormat.SampleRate,
                audioFormat.Channels),
            cancellationToken).ConfigureAwait(false);

        progress.Report(new VideoExportProgress("Audio", 0, 0));
        int audioChunks = 0;
        await offlineAudio.RenderAsync(
            audioPlan,
            async (chunk, token) =>
            {
                token.ThrowIfCancellationRequested();
                await mux.WriteAudioChunkAsync(chunk, token).ConfigureAwait(false);
                audioChunks++;
                progress.Report(new VideoExportProgress("Audio", audioChunks, 0));
            },
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new VideoExportProgress("Video", 0, 0));

        await mux.BeginVideoAsync(cancellationToken).ConfigureAwait(false);

        int writtenVideoFrames = 0;

        var framePipeline = new VideoExportFramePipeline(frameRenderer);
        progress.Report(new VideoExportProgress("Video", 0, totalVideoFrames));
        await framePipeline.RenderAsync(
            videoPlan,
            async (_, rendered, token) =>
            {
                token.ThrowIfCancellationRequested();
                await mux.WriteVideoFrameAsync(rendered.Rgb, token).ConfigureAwait(false);
                writtenVideoFrames++;
                progress.Report(
                    new VideoExportProgress(
                        "Video",
                        writtenVideoFrames,
                        totalVideoFrames));
            },
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(new VideoExportProgress("Encoding", 0, 1));
        await mux.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(new VideoExportProgress("Encoding", 1, 1));
    }
}
