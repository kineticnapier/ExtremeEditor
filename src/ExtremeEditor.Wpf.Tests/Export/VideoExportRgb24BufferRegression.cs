using System.IO;
using ExtremeEditor.Audio;
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
        VerifyInvalidAudioRangeRejectedBeforeCreatingOutputDirectory();
        VerifyInvalidOutputPathRejectedBeforeNativePreparation();
        VerifyDirectoryOutputPathRejectedBeforeNativePreparation();
        VerifyInvalidSongPathRejectedBeforeNativePreparation();
        VerifyDirectorySongPathRejectedBeforeNativePreparation();
        VerifyOutputPathCannotReplaceExportInputs();
        VerifyNativePreflightFailureDoesNotCreateOutputDirectory();
        VerifyCancellationFromNativeFactoryDoesNotStartExport();
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

    private static void VerifyInvalidAudioRangeRejectedBeforeCreatingOutputDirectory()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        TimingMap timingMap = TimingMapBuilder.Build(level);
        HitSoundTimeline hitSounds = HitSoundTimelineBuilder.Build(level);
        // The chart range is valid, but the chart-to-audio conversion is not.
        // This must fail before the encoder creates its output directory.
        level.OffsetMilliseconds = double.NaN;

        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorVideoPreflight",
            Guid.NewGuid().ToString("N"));
        var request = new VideoExportRequest(
            new VideoExportSource(level, timingMap, hitSounds, SongPath: null),
            OutputPath: Path.Combine(outputDirectory, "export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        bool rejected = false;
        bool outputDirectoryCreated = false;
        try
        {
            try
            {
                new VideoExportService().ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (ArgumentOutOfRangeException ex)
                when (ex.ParamName == nameof(OfflineAudioRenderPlan.StartAudioTimeSeconds))
            {
                rejected = true;
            }
        }
        finally
        {
            outputDirectoryCreated = Directory.Exists(outputDirectory);
            if (outputDirectoryCreated)
                Directory.Delete(outputDirectory, recursive: true);
        }

        if (!rejected)
            throw new InvalidOperationException(
                "An invalid mapped audio range must be rejected during export preflight.");
        if (outputDirectoryCreated)
            throw new InvalidOperationException(
                "Invalid audio timing created the output directory before rejection.");
    }

    private static void VerifyInvalidOutputPathRejectedBeforeNativePreparation()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorInvalidOutputPreflight",
            Guid.NewGuid().ToString("N"));
        var request = new VideoExportRequest(
            new VideoExportSource(
                level,
                TimingMapBuilder.Build(level),
                HitSoundTimelineBuilder.Build(level),
                SongPath: null),
            OutputPath: Path.Combine(outputDirectory, "invalid\0export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        int nativeFactoryCalls = 0;
        var service = new VideoExportService((_, _) =>
        {
            nativeFactoryCalls++;
            throw new InvalidOperationException("Native renderer must not be initialized.");
        });
        bool rejected = false;
        bool outputDirectoryCreated = false;
        try
        {
            try
            {
                service.ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
        }
        finally
        {
            outputDirectoryCreated = Directory.Exists(outputDirectory);
            if (outputDirectoryCreated)
                Directory.Delete(outputDirectory, recursive: true);
        }

        if (!rejected || nativeFactoryCalls != 0)
            throw new InvalidOperationException(
                "Invalid output path was not rejected before native preparation.");
        if (outputDirectoryCreated)
            throw new InvalidOperationException(
                "Invalid output path created the video output directory.");
    }

    private static void VerifyDirectoryOutputPathRejectedBeforeNativePreparation()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorDirectoryOutputPreflight",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        int nativeFactoryCalls = 0;
        var service = new VideoExportService((_, _) =>
        {
            nativeFactoryCalls++;
            throw new InvalidOperationException("Native renderer must not be initialized.");
        });

        try
        {
            // Existing directory and a not-yet-created directory-shaped path
            // must both fail before any native work or output staging.
            foreach (string invalidPath in new[]
            {
                outputDirectory,
                Path.Combine(outputDirectory, "future") + Path.DirectorySeparatorChar
            })
            {
                var request = new VideoExportRequest(
                    new VideoExportSource(
                        level,
                        TimingMapBuilder.Build(level),
                        HitSoundTimelineBuilder.Build(level),
                        SongPath: null),
                    OutputPath: invalidPath,
                    FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
                    StartChartTimeSeconds: 0.0,
                    EndChartTimeSeconds: 1.0,
                    FramesPerSecond: 60,
                    Width: 2,
                    Height: 2,
                    SongVolumePercent: 100.0,
                    HitSoundsEnabled: false);

                bool rejected = false;
                try
                {
                    service.ExportAsync(
                        request, new Progress<VideoExportProgress>(),
                        CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (ArgumentException ex)
                    when (ex.ParamName == nameof(VideoExportRequest.OutputPath))
                {
                    rejected = true;
                }

                if (!rejected || nativeFactoryCalls != 0)
                    throw new InvalidOperationException(
                        "Directory-shaped video output path was not rejected before native preparation.");
                if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    throw new InvalidOperationException(
                        "Invalid directory output path created export staging files.");
            }
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static void VerifyInvalidSongPathRejectedBeforeNativePreparation()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorInvalidSongPreflight",
            Guid.NewGuid().ToString("N"));
        var request = new VideoExportRequest(
            new VideoExportSource(
                level,
                TimingMapBuilder.Build(level),
                HitSoundTimelineBuilder.Build(level),
                SongPath: "invalid\0song.wav"),
            OutputPath: Path.Combine(outputDirectory, "export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        int nativeFactoryCalls = 0;
        var service = new VideoExportService((_, _) =>
        {
            nativeFactoryCalls++;
            throw new InvalidOperationException("Native renderer must not be initialized.");
        });
        bool rejected = false;
        bool outputDirectoryCreated = false;
        try
        {
            try
            {
                service.ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
        }
        finally
        {
            outputDirectoryCreated = Directory.Exists(outputDirectory);
            if (outputDirectoryCreated)
                Directory.Delete(outputDirectory, recursive: true);
        }

        if (!rejected || nativeFactoryCalls != 0)
            throw new InvalidOperationException(
                "Invalid song path was not rejected before native preparation.");
        if (outputDirectoryCreated)
            throw new InvalidOperationException(
                "Invalid song path created the video output directory.");
    }

    private static void VerifyDirectorySongPathRejectedBeforeNativePreparation()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        string songDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorDirectorySongPreflight",
            Guid.NewGuid().ToString("N"));
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorDirectorySongOutput",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(songDirectory);

        int nativeFactoryCalls = 0;
        var service = new VideoExportService((_, _) =>
        {
            nativeFactoryCalls++;
            throw new InvalidOperationException("Native renderer must not be initialized.");
        });
        var request = new VideoExportRequest(
            new VideoExportSource(
                level,
                TimingMapBuilder.Build(level),
                HitSoundTimelineBuilder.Build(level),
                SongPath: songDirectory),
            OutputPath: Path.Combine(outputDirectory, "export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        bool rejected = false;
        try
        {
            try
            {
                service.ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (ArgumentException ex)
                when (ex.ParamName == nameof(VideoExportSource.SongPath))
            {
                rejected = true;
            }

            if (!rejected || nativeFactoryCalls != 0)
                throw new InvalidOperationException(
                    "A directory song path must be rejected before native preparation.");
            if (Directory.Exists(outputDirectory))
                throw new InvalidOperationException(
                    "A directory song path created the video output directory.");
        }
        finally
        {
            Directory.Delete(songDirectory, recursive: true);
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static void VerifyOutputPathCannotReplaceExportInputs()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        string directory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorVideoInputCollision",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string songPath = Path.Combine(directory, "song.wav");
        string ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
        byte[] originalSong = [1, 2, 3, 4];
        byte[] originalFfmpeg = [5, 6, 7, 8];
        File.WriteAllBytes(songPath, originalSong);
        File.WriteAllBytes(ffmpegPath, originalFfmpeg);

        int nativeFactoryCalls = 0;
        var service = new VideoExportService((_, _) =>
        {
            nativeFactoryCalls++;
            throw new InvalidOperationException("Native renderer must not be initialized.");
        });
        try
        {
            // Use alternate spellings to verify full-path and Windows
            // case-insensitive comparison, not only identical input strings.
            foreach (string outputPath in new[]
            {
                Path.Combine(directory, ".", "SONG.WAV"),
                Path.Combine(directory, ".", "FFMPEG.EXE")
            })
            {
                var request = new VideoExportRequest(
                    new VideoExportSource(
                        level,
                        TimingMapBuilder.Build(level),
                        HitSoundTimelineBuilder.Build(level),
                        SongPath: songPath),
                    OutputPath: outputPath,
                    FfmpegPath: ffmpegPath,
                    StartChartTimeSeconds: 0.0,
                    EndChartTimeSeconds: 1.0,
                    FramesPerSecond: 60,
                    Width: 2,
                    Height: 2,
                    SongVolumePercent: 100.0,
                    HitSoundsEnabled: false);

                bool rejected = false;
                try
                {
                    service.ExportAsync(
                        request, new Progress<VideoExportProgress>(),
                        CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (ArgumentException ex)
                    when (ex.ParamName == nameof(VideoExportRequest.OutputPath))
                {
                    rejected = true;
                }

                if (!rejected || nativeFactoryCalls != 0)
                    throw new InvalidOperationException(
                        "Video export input/output path collision was not rejected before native preparation.");
            }

            if (!File.ReadAllBytes(songPath).SequenceEqual(originalSong) ||
                !File.ReadAllBytes(ffmpegPath).SequenceEqual(originalFfmpeg))
                throw new InvalidOperationException(
                    "Rejected video export modified an input file.");
            if (Directory.GetFiles(directory).Length != 2)
                throw new InvalidOperationException(
                    "Rejected video export created staging files beside the inputs.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyNativePreflightFailureDoesNotCreateOutputDirectory()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        var source = new VideoExportSource(
            level,
            TimingMapBuilder.Build(level),
            HitSoundTimelineBuilder.Build(level),
            SongPath: null);
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorNativeVideoPreflight",
            Guid.NewGuid().ToString("N"));
        var request = new VideoExportRequest(
            Source: source,
            OutputPath: Path.Combine(outputDirectory, "export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        int factoryCalls = 0;
        bool rejected = false;
        bool outputDirectoryCreated = false;
        var service = new VideoExportService((_, _) =>
        {
            factoryCalls++;
            throw new InvalidOperationException("Injected native preparation failure.");
        });
        try
        {
            try
            {
                service.ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
                when (ex.Message == "Injected native preparation failure.")
            {
                rejected = true;
            }
        }
        finally
        {
            outputDirectoryCreated = Directory.Exists(outputDirectory);
            if (outputDirectoryCreated)
                Directory.Delete(outputDirectory, recursive: true);
        }

        if (!rejected || factoryCalls != 1)
            throw new InvalidOperationException(
                "Native preparation failure was not propagated exactly once.");
        if (outputDirectoryCreated)
            throw new InvalidOperationException(
                "Native preparation failure created an output directory before rejection.");
    }

    private static void VerifyCancellationFromNativeFactoryDoesNotStartExport()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        var source = new VideoExportSource(
            level,
            TimingMapBuilder.Build(level),
            HitSoundTimelineBuilder.Build(level),
            SongPath: null);
        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "ExtremeEditorCanceledNativePreflight",
            Guid.NewGuid().ToString("N"));
        var request = new VideoExportRequest(
            Source: source,
            OutputPath: Path.Combine(outputDirectory, "export.mp4"),
            FfmpegPath: typeof(VideoExportRgb24BufferRegression).Assembly.Location,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: 1.0,
            FramesPerSecond: 60,
            Width: 2,
            Height: 2,
            SongVolumePercent: 100.0,
            HitSoundsEnabled: false);

        using var cancellation = new CancellationTokenSource();
        int factoryCalls = 0;
        bool canceled = false;
        bool outputDirectoryCreated = false;
        var service = new VideoExportService((width, height) =>
        {
            factoryCalls++;
            // Cancellation after the initial check must be observed before
            // the returned renderer starts expensive native scene loading.
            cancellation.Cancel();
            return new NativeChartFrameRenderer(width, height);
        });

        try
        {
            try
            {
                service.ExportAsync(
                    request, new Progress<VideoExportProgress>(),
                    cancellation.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
        }
        finally
        {
            outputDirectoryCreated = Directory.Exists(outputDirectory);
            if (outputDirectoryCreated)
                Directory.Delete(outputDirectory, recursive: true);
        }

        if (!canceled || factoryCalls != 1)
            throw new InvalidOperationException(
                "Cancellation from the native renderer factory was not propagated.");
        if (outputDirectoryCreated)
            throw new InvalidOperationException(
                "Canceled native preparation created an output directory.");
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
