using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Export;

internal sealed record VideoExportRequest(
    string ChartPath,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(ChartPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        if (!File.Exists(ChartPath))
            throw new FileNotFoundException("ADOFAI chart was not found.", ChartPath);
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
    internal async ValueTask ExportAsync(
        VideoExportRequest request,
        IProgress<VideoExportProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        AdoFaiLoadResult loaded = AdoFaiLoader.Load(Path.GetFullPath(request.ChartPath));
        LevelDocument level = loaded.Document;
        TimingMap timingMap = FlatTimingMapBuilder.Build(level);
        HitSoundTimeline hitSoundTimeline = HitSoundTimelineBuilder.Build(level);

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

        string? songPath = level.ResolveSongPath();
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

        using var frameRenderer = new NativeChartFrameRenderer(
            request.Width,
            request.Height);
        frameRenderer.Load(request.ChartPath);
        frameRenderer.SetCleanView(true);

        var videoPlan = new VideoExportPlan(
            startChart,
            endChart,
            request.FramesPerSecond,
            request.Width,
            request.Height);
        int totalVideoFrames = VideoExportTimeline.Build(videoPlan).Count;
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

        var audioPlan = new OfflineAudioRenderPlan(
            level,
            timingMap,
            hitSoundTimeline,
            songPath,
            Math.Max(0.0, startAudio),
            Math.Max(0.0, endAudio),
            request.SongVolumePercent,
            request.HitSoundsEnabled);

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
        progress.Report(new VideoExportProgress("Encoding", 0, 1));
        await mux.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(new VideoExportProgress("Encoding", 1, 1));
    }
}
