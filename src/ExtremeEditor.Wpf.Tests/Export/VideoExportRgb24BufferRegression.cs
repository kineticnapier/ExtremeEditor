using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportRgb24BufferRegression
{
    internal static void Run()
    {
        const int expected1080pBytes = 1920 * 1080 * 3;
        if (VideoExportRgb24Buffer.GetRequiredByteCount(1920, 1080) != expected1080pBytes)
            throw new InvalidOperationException("1080p RGB24 frame size changed.");

        new VideoExportPlan(0.0, 1.0, 60, 1920, 1080).Validate();
        new VideoExportPlan(0.0, 1.0, 60, 1, 1).Validate();

        RequireRejected(0, 1080);
        RequireRejected(1920, 0);
        RequireRejected(int.MaxValue, 1); // RGB24 stride cannot fit in int.
        RequireRejected(50_000, 50_000); // Valid stride, oversized frame.
        RequireRejected(int.MaxValue, int.MaxValue); // Must not overflow long.

        // The public renderer can also be used without VideoExportRequest.
        // Invalid dimensions must fail before native renderer initialization.
        RequireRendererRejected(0, 1080);
        RequireRendererRejected(int.MaxValue, 1);
        RequireRendererRejected(50_000, 50_000);

        // A non-finite volume must fail request validation, not after
        // the encoder has already created its temporary output files.
        VerifyNonFiniteVideoExportVolumeRejected();
    }

    private static void VerifyNonFiniteVideoExportVolumeRejected()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        var source = new VideoExportSource(
            level,
            TimingMapBuilder.Build(level),
            HitSoundTimelineBuilder.Build(level),
            SongPath: null);
        var valid = new VideoExportRequest(
            Source: source,
            OutputPath: "volume-regression.mp4",
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 1920,
            Height: 1080,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);
        valid.Validate();
        (valid with { SongVolumePercent = 0.0 }).Validate();

        foreach (double volume in new[] {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            try
            {
                (valid with { SongVolumePercent = volume }).Validate();
            }
            catch (ArgumentOutOfRangeException ex)
                when (ex.ParamName == nameof(VideoExportRequest.SongVolumePercent))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Non-finite video export volume {volume} passed request validation.");
        }
    }

    private static void RequireRendererRejected(int width, int height)
    {
        try
        {
            var renderer = new NativeChartFrameRenderer(width, height);
            renderer.Dispose();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Direct RGB24 renderer must reject unsupported dimensions {width}x{height}.");
    }

    private static void RequireRejected(int width, int height)
    {
        try
        {
            new VideoExportPlan(0.0, 1.0, 60, width, height).Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"RGB24 export must reject unsupported dimensions {width}x{height}.");
    }
}
