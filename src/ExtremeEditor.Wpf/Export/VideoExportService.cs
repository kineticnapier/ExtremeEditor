using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Export;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        if (!File.Exists(FfmpegPath))
            throw new FileNotFoundException("ffmpeg executable was not found.", FfmpegPath);
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
                renderer.Load(level, timingMap);
                renderer.SetCleanView(true);
                return renderer;
            }
            catch
            {
                renderer.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        var offlineAudio = new OfflineAudioRenderer();
        OfflineAudioFormat audioFormat = offlineAudio.ProbeFormat(songPath);

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
