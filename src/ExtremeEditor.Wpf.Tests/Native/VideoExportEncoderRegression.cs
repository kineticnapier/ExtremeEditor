using System.IO;
using System.Reflection;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportEncoderRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;

        Type? encoderType = assembly.GetType("ExtremeEditor.Wpf.Export.IVideoEncoder");
        Type? startInfoType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoEncoderStartInfo");
        Type? muxPipelineType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportMuxPipeline");

        var missing = new List<string>();
        if (encoderType is null)
            missing.Add("IVideoEncoder is missing");
        if (startInfoType is null)
            missing.Add("VideoEncoderStartInfo is missing");
        if (muxPipelineType is null)
            missing.Add("VideoExportMuxPipeline is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4 encoder contract is not available:\n- " +
                string.Join("\n- ", missing));
        }

        VerifyEncoderContract(encoderType!, startInfoType!);
        VerifyMuxPipelineContract(muxPipelineType!, encoderType!);
        VerifyCompletionRequiresVideoPhase();
        VerifyPhaseStartsRejectReentrancy();
        VerifyWritesRejectReentrantOperations();
        VerifyConcurrentPhaseStartsRejectOverlap();
        VerifyConcurrentWritesRejectOverlap();
        VerifyCancellationDuringPhaseTransitions();
        VerifyCancellationDuringCompletion();
        VerifyCompletionRejectsReentrantWrites();
        VerifyConcurrentCompletionRejectsOverlap();
        VerifyCancellationDuringChunkWrites();
        VerifyEncoderExceptionsAreTerminal();
        VerifyPreCanceledOperationsAreRetryable();
        VerifyMissingFfmpegRejectedBeforeEncoderStart();
        VerifyDirectoryOutputRejectedBeforeEncoderStart();
        VerifyAmbiguousOutputPathsRejectedBeforeEncoderStart();
        VerifyLongOutputFilenameUsesShortStagingName();
        VerifyReadOnlyOutputRejectedBeforeEncoderStart();
        VerifyDirectEncoderProtectsFfmpegExecutable();
        VerifySourceContract();
    }

    private static void VerifyMissingFfmpegRejectedBeforeEncoderStart()
    {
        // Direct encoder callers must fail before staging audio or creating
        // the destination directory if ffmpeg is absent.
        string root = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-MissingFfmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string ffmpeg = Path.Combine(root, "missing-ffmpeg.exe");
            string outputDirectory = Path.Combine(root, "not-created");
            string output = Path.Combine(outputDirectory, "video.mp4");
            var encoder = new FfmpegVideoEncoder(ffmpeg);
            try
            {
                bool rejected = false;
                try
                {
                    encoder.BeginAsync(
                        new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (FileNotFoundException ex) when (
                    string.Equals(ex.FileName, Path.GetFullPath(ffmpeg),
                        StringComparison.OrdinalIgnoreCase))
                {
                    rejected = true;
                }

                if (!rejected)
                    throw new InvalidOperationException(
                        "Direct ffmpeg encoder accepted a missing executable.");
                if (Directory.Exists(outputDirectory) ||
                    Directory.EnumerateFileSystemEntries(root).Any())
                    throw new InvalidOperationException(
                        "Missing ffmpeg created an output directory or staging files.");
            }
            finally
            {
                encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyDirectEncoderProtectsFfmpegExecutable()
    {
        // Service callers already reject this output path. A direct encoder
        // caller must also refuse to replace the executable before staging.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-EncoderFfmpegGuard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            const string sentinel = "original ffmpeg executable";
            File.WriteAllText(ffmpeg, sentinel);

            string[] protectedOutputs =
            [
                ffmpeg,
                Path.Combine(directory, "unused", "..", "ffmpeg.exe"),
                ffmpeg.ToUpperInvariant()
            ];
            foreach (string output in protectedOutputs)
            {
                var encoder = new FfmpegVideoEncoder(ffmpeg);
                try
                {
                    bool rejected = false;
                    try
                    {
                        encoder.BeginAsync(
                            new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (ArgumentException ex) when (
                        ex.ParamName == nameof(VideoEncoderStartInfo.OutputPath))
                    {
                        rejected = true;
                    }

                    if (!rejected)
                        throw new InvalidOperationException(
                            "Direct ffmpeg encoder accepted its executable as the video destination.");
                }
                finally
                {
                    encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            if (File.ReadAllText(ffmpeg) != sentinel ||
                Directory.EnumerateFileSystemEntries(directory).Count() != 1)
            {
                throw new InvalidOperationException(
                    "Rejected ffmpeg executable output modified the executable or created staging files.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyDirectoryOutputRejectedBeforeEncoderStart()
    {
        // The direct encoder API must enforce the same destination rule as
        // VideoExportRequest, before creating any staging files.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-EncoderDirectory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string[] invalidOutputs =
            [
                directory,
                Path.Combine(directory, "not-created") + Path.DirectorySeparatorChar
            ];
            foreach (string output in invalidOutputs)
            {
                var encoder = new FfmpegVideoEncoder(
                    Path.Combine(directory, "unused-ffmpeg.exe"));
                try
                {
                    bool rejected = false;
                    try
                    {
                        encoder.BeginAsync(
                            new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (ArgumentException ex) when (
                        ex.ParamName == nameof(VideoEncoderStartInfo.OutputPath))
                    {
                        rejected = true;
                    }

                    if (!rejected)
                        throw new InvalidOperationException(
                            "Direct ffmpeg encoder accepted a directory output path.");
                }
                finally
                {
                    encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException(
                    "Rejected directory output created staging files or folders.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyAmbiguousOutputPathsRejectedBeforeEncoderStart()
    {
        // Win32 aliases trailing dots/spaces and reserves device names even
        // with extensions. Neither case may create export staging files.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-AmbiguousVideo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            const string sentinel = "original executable";
            File.WriteAllText(ffmpeg, sentinel);

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            var source = new VideoExportSource(
                level,
                TimingMapBuilder.Build(level),
                HitSoundTimelineBuilder.Build(level),
                null);
            string[] invalidOutputs =
            [
                Path.Combine(directory, "video.mp4."),
                Path.Combine(directory, "video.mp4 "),
                Path.Combine(directory, "alias.", "video.mp4"),
                Path.Combine(directory, "alias ", "video.mp4"),
                ffmpeg + ".",
                ffmpeg + " ",
                Path.Combine(directory, "CON.mp4"),
                Path.Combine(directory, "NUL.mp4"),
                Path.Combine(directory, "PRN", "video.mp4"),
                Path.Combine(directory, "AUX.txt"),
                Path.Combine(directory, "COM1.mp4"),
                Path.Combine(directory, "LPT9", "video.mp4"),
                Path.Combine(directory, "COM².mp4"),
                Path.Combine(directory, "CONOUT$.mp4"),
                // NTFS alternate data streams must never be export targets.
                Path.Combine(directory, "video.mp4:notes"),
                Path.Combine(directory, "video.mp4::$DATA"),
                Path.Combine(directory, "folder:notes", "video.mp4"),
                // Win32-invalid filename characters must fail before audio,
                // native preparation, or encoder staging begins.
                directory + Path.DirectorySeparatorChar + "video?.mp4",
                directory + Path.DirectorySeparatorChar + "video*.mp4",
                directory + Path.DirectorySeparatorChar + "video<.mp4",
                directory + Path.DirectorySeparatorChar + "video>.mp4",
                directory + Path.DirectorySeparatorChar + "video|.mp4",
                directory + Path.DirectorySeparatorChar + "video\".mp4",
                directory + Path.DirectorySeparatorChar + "video\u001F.mp4",
                directory + Path.DirectorySeparatorChar + "folder?" +
                    Path.DirectorySeparatorChar + "video.mp4",
                // \\?\ paths do not support the ordinary . / .. segments.
                // Reject before any native or ffmpeg output preparation.
                @"\\?\C:\exports\.\video.mp4",
                @"\\?\C:\exports\..\video.mp4",
                @"\\?\UNC\server\share\..\video.mp4",
                // Device namespace paths must not be treated as ordinary files.
                @"\\\\.\\C:\\exports\\video.mp4",
                @"\\\\.\\pipe\\video.mp4",
                @"\\\\?\\GLOBALROOT\\Device\\example\\video.mp4",
                // C:CON.mp4 is drive-relative, but still resolves to CON.mp4.
                @"C:CON.mp4",
                @"C:NUL.mp4",
                @"C:COM1.mp4"
            ];

            // Do not reject ordinary names merely because they begin with a
            // device-name prefix or contain a non-reserved port number.
            foreach (string safe in new[]
                     {
                         Path.Combine(directory, "company.mp4"),
                         Path.Combine(directory, "COM0.mp4"),
                         Path.Combine(directory, "LPT10.mp4"),
                         // Ordinary paths still normalize dot segments.
                         Path.Combine(directory, ".", "video.mp4"),
                         Path.Combine(directory, "unused", "..", "video.mp4"),
                         @"C:\video.mp4",
                         @"C:relative.mp4",
                         @"C:company.mp4",
                         @"C:COM0.mp4",
                         // The question mark here is an extended-path prefix,
                         // not an invalid filename character.
                         @"\\?\C:\video.mp4"
                     })
            {
                if (VideoExportOutputPathGuard.HasAmbiguousWindowsComponent(safe))
                    throw new InvalidOperationException(
                        $"Video export path guard rejected a normal name: {safe}");
            }

            foreach (string output in invalidOutputs)
            {
                var request = new VideoExportRequest(
                    source, output, ffmpeg, 0.0, 1.0, 30, 2, 2, 100.0, true);
                bool requestRejected = false;
                try
                {
                    request.Validate();
                }
                catch (ArgumentException ex) when (
                    ex.ParamName == nameof(VideoExportRequest.OutputPath))
                {
                    requestRejected = true;
                }
                if (!requestRejected)
                    throw new InvalidOperationException(
                        $"Video export request accepted an ambiguous path: {output}");

                var encoder = new FfmpegVideoEncoder(ffmpeg);
                try
                {
                    bool encoderRejected = false;
                    try
                    {
                        encoder.BeginAsync(
                            new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (ArgumentException ex) when (
                        ex.ParamName == nameof(VideoEncoderStartInfo.OutputPath))
                    {
                        encoderRejected = true;
                    }
                    if (!encoderRejected)
                        throw new InvalidOperationException(
                            $"Direct ffmpeg encoder accepted an ambiguous path: {output}");
                }
                finally
                {
                    encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            if (File.ReadAllText(ffmpeg) != sentinel ||
                Directory.EnumerateFileSystemEntries(directory).Count() != 1)
                throw new InvalidOperationException(
                    "Rejected output names modified the executable or created staging files.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyLongOutputFilenameUsesShortStagingName()
    {
        // NTFS accepts an output basename close to 255 characters, but
        // prepending that basename to a GUID staging filename would exceed
        // the per-component limit before ffmpeg can create the MP4.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-LongVideo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            File.WriteAllText(ffmpeg, "test executable");
            string output = Path.Combine(directory, new string('v', 220) + ".mp4");
            var encoder = new FfmpegVideoEncoder(ffmpeg);
            try
            {
                encoder.BeginAsync(
                    new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                    CancellationToken.None).GetAwaiter().GetResult();

                FieldInfo field = typeof(FfmpegVideoEncoder).GetField(
                    "_videoTempPath", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Video staging path field is missing.");
                string staging = field.GetValue(encoder) as string
                    ?? throw new InvalidOperationException("Video staging path was not prepared.");
                string stagingName = Path.GetFileName(staging);
                if (!string.Equals(Path.GetDirectoryName(staging), directory,
                        StringComparison.OrdinalIgnoreCase) ||
                    stagingName.Length > 100 ||
                    !stagingName.EndsWith(".tmp.mp4", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Video staging must remain in the destination directory with a bounded filename.");
                }

                if (File.Exists(output))
                    throw new InvalidOperationException(
                        "Beginning a video export unexpectedly created the final output.");
            }
            finally
            {
                encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyReadOnlyOutputRejectedBeforeEncoderStart()
    {
        // A read-only destination must not be overwritten or cause temporary
        // staging files to be created by either the service or direct encoder.
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-ReadOnlyVideo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "protected.mp4");
        try
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            const string sentinel = "original video output";
            File.WriteAllText(ffmpeg, "test executable");
            File.WriteAllText(output, sentinel);
            File.SetAttributes(output, File.GetAttributes(output) | FileAttributes.ReadOnly);

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            var source = new VideoExportSource(
                level,
                TimingMapBuilder.Build(level),
                HitSoundTimelineBuilder.Build(level),
                null);
            var request = new VideoExportRequest(
                source, output, ffmpeg, 0.0, 1.0, 30, 2, 2, 100.0, true);
            bool serviceRejected = false;
            try
            {
                request.Validate();
            }
            catch (ArgumentException ex) when (
                ex.ParamName == nameof(VideoExportRequest.OutputPath))
            {
                serviceRejected = true;
            }
            if (!serviceRejected)
                throw new InvalidOperationException(
                    "Video export request accepted a read-only destination.");

            var encoder = new FfmpegVideoEncoder(ffmpeg);
            try
            {
                bool encoderRejected = false;
                try
                {
                    encoder.BeginAsync(
                        new VideoEncoderStartInfo(output, 2, 2, 30, 48000, 2),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (ArgumentException ex) when (
                    ex.ParamName == nameof(VideoEncoderStartInfo.OutputPath))
                {
                    encoderRejected = true;
                }
                if (!encoderRejected)
                    throw new InvalidOperationException(
                        "Direct ffmpeg encoder accepted a read-only destination.");
            }
            finally
            {
                encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (File.ReadAllText(output) != sentinel ||
                Directory.EnumerateFileSystemEntries(directory).Count() != 2)
                throw new InvalidOperationException(
                    "Read-only destination preflight modified output or created staging files.");
        }
        finally
        {
            if (File.Exists(output))
                File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.ReadOnly);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyEncoderContract(Type encoderType, Type startInfoType)
    {
        if (!encoderType.IsInterface)
            throw new InvalidOperationException("IVideoEncoder must be an interface.");

        RequireMethod(
            encoderType,
            "BeginAsync",
            typeof(ValueTask),
            [startInfoType, typeof(CancellationToken)]);

        RequireMethod(
            encoderType,
            "WriteVideoFrameAsync",
            typeof(ValueTask),
            [typeof(byte[]), typeof(CancellationToken)]);

        Type audioChunkType = typeof(ExtremeEditor.Audio.OfflineAudioChunk);
        RequireMethod(
            encoderType,
            "WriteAudioChunkAsync",
            typeof(ValueTask),
            [audioChunkType, typeof(CancellationToken)]);

        RequireMethod(
            encoderType,
            "CompleteAsync",
            typeof(ValueTask),
            [typeof(CancellationToken)]);
    }

    private static void VerifyMuxPipelineContract(Type pipelineType, Type encoderType)
    {
        ConstructorInfo? constructor = pipelineType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [encoderType],
            modifiers: null);

        if (constructor is null)
        {
            throw new InvalidOperationException(
                "VideoExportMuxPipeline must accept IVideoEncoder via constructor injection.");
        }

        MethodInfo? writeVideo = pipelineType.GetMethod(
            "WriteVideoFrameAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo? writeAudio = pipelineType.GetMethod(
            "WriteAudioChunkAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (writeVideo is null || writeVideo.ReturnType != typeof(ValueTask))
            throw new InvalidOperationException(
                "VideoExportMuxPipeline.WriteVideoFrameAsync is missing or not streaming.");

        if (writeAudio is null || writeAudio.ReturnType != typeof(ValueTask))
            throw new InvalidOperationException(
                "VideoExportMuxPipeline.WriteAudioChunkAsync is missing or not streaming.");
    }

    private static void RequireMethod(
        Type type,
        string name,
        Type returnType,
        Type[] parameterTypes)
    {
        MethodInfo? method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null || method.ReturnType != returnType)
        {
            throw new InvalidOperationException(
                $"{type.Name}.{name} has the wrong streaming contract.");
        }
    }

    private static void VerifyPhaseStartsRejectReentrancy()
    {
        // An encoder can call back into the mux before its BeginAsync or
        // BeginVideoAsync returns. A second start must not reach the encoder.
        var startInfo = new VideoEncoderStartInfo(
            "reentrant-start-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        VideoExportMuxPipeline? mux = null;
        bool beginRejected = false;
        bool videoBeginRejected = false;
        bool audioRejected = false;
        int beginCallbacks = 0;
        int videoBeginCallbacks = 0;
        var encoder = new RecordingVideoEncoder
        {
            OnBegin = () =>
            {
                if (++beginCallbacks != 1)
                    return;
                try
                {
                    mux!.BeginAsync(startInfo, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    beginRejected = true;
                }
            },
            OnBeginVideo = () =>
            {
                if (++videoBeginCallbacks != 1)
                    return;
                try
                {
                    mux!.BeginVideoAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    videoBeginRejected = true;
                }

                // The audio phase is ending while BeginVideoAsync is pending.
                try
                {
                    mux!.WriteAudioChunkAsync(chunk, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    audioRejected = true;
                }
            }
        };
        mux = new VideoExportMuxPipeline(encoder);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        if (!beginRejected || encoder.BeginCalls != 1)
            throw new InvalidOperationException(
                "Mux allowed reentrant BeginAsync to start the encoder twice.");

        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!videoBeginRejected || !audioRejected ||
            encoder.BeginVideoCalls != 1 || encoder.AudioChunkCalls != 0)
            throw new InvalidOperationException(
                "Mux allowed reentrant video start or audio writes during phase transition.");

        // A rejected reentrant operation must not break normal video export.
        mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
            .GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.VideoFrameCalls != 1 || encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux was not usable after rejecting reentrant phase starts.");
    }

    private static void VerifyWritesRejectReentrantOperations()
    {
        // A custom encoder can invoke the mux again before returning from a
        // write. Neither nested writes nor phase transitions may overtake it.
        var startInfo = new VideoEncoderStartInfo(
            "reentrant-write-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        VideoExportMuxPipeline? mux = null;
        bool nestedAudioRejected = false;
        bool earlyVideoPhaseRejected = false;
        bool nestedVideoRejected = false;
        bool earlyCompletionRejected = false;
        int audioCallbacks = 0;
        int videoCallbacks = 0;
        var encoder = new RecordingVideoEncoder
        {
            OnWriteAudio = () =>
            {
                if (++audioCallbacks != 1)
                    return;
                try
                {
                    mux!.WriteAudioChunkAsync(chunk, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    nestedAudioRejected = true;
                }

                try
                {
                    mux!.BeginVideoAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    earlyVideoPhaseRejected = true;
                }
            },
            OnWriteVideo = () =>
            {
                if (++videoCallbacks != 1)
                    return;
                try
                {
                    mux!.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    nestedVideoRejected = true;
                }

                try
                {
                    mux!.CompleteAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    earlyCompletionRejected = true;
                }
            }
        };

        mux = new VideoExportMuxPipeline(encoder);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        mux.WriteAudioChunkAsync(chunk, CancellationToken.None).GetAwaiter().GetResult();
        if (!nestedAudioRejected || !earlyVideoPhaseRejected ||
            encoder.AudioChunkCalls != 1 || encoder.BeginVideoCalls != 0)
        {
            throw new InvalidOperationException(
                "Mux allowed a nested audio write or video-phase start during an audio write.");
        }

        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!nestedVideoRejected || !earlyCompletionRejected ||
            encoder.VideoFrameCalls != 1 || encoder.CompleteCalls != 0)
        {
            throw new InvalidOperationException(
                "Mux allowed a nested video write or completion during a frame write.");
        }

        // Rejected callbacks must not prevent subsequent normal completion.
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.BeginVideoCalls != 1 || encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux did not recover after rejecting reentrant writes.");
    }

    private static void VerifyConcurrentPhaseStartsRejectOverlap()
    {
        // The initial encoder calls may be suspended on a different thread.
        // Other callers must not enter the same phase or advance past it.
        var startInfo = new VideoEncoderStartInfo(
            "concurrent-phase-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        using var beginEntered = new ManualResetEventSlim();
        using var releaseBegin = new ManualResetEventSlim();
        using var videoEntered = new ManualResetEventSlim();
        using var releaseVideo = new ManualResetEventSlim();
        var encoder = new RecordingVideoEncoder
        {
            OnBegin = () =>
            {
                beginEntered.Set();
                if (!releaseBegin.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Concurrent begin test did not release the encoder.");
            },
            OnBeginVideo = () =>
            {
                videoEntered.Set();
                if (!releaseVideo.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Concurrent video start test did not release the encoder.");
            }
        };
        var mux = new VideoExportMuxPipeline(encoder);

        Task begin = Task.Run(async () =>
            await mux.BeginAsync(startInfo, CancellationToken.None));
        try
        {
            if (!beginEntered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent begin never entered the encoder.");

            AssertConcurrentCallsRejected(
                () => mux.BeginAsync(startInfo, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "second encoder begin");
            AssertConcurrentCallsRejected(
                () => mux.BeginVideoAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "video phase before encoder begin completes");
            AssertConcurrentCallsRejected(
                () => mux.WriteAudioChunkAsync(chunk, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "audio before encoder begin completes");
            if (encoder.BeginCalls != 1 || encoder.BeginVideoCalls != 0 ||
                encoder.AudioChunkCalls != 0)
                throw new InvalidOperationException(
                    "Concurrent calls overtook the pending encoder begin.");
        }
        finally
        {
            releaseBegin.Set();
            begin.GetAwaiter().GetResult();
        }

        Task videoBegin = Task.Run(async () =>
            await mux.BeginVideoAsync(CancellationToken.None));
        try
        {
            if (!videoEntered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent video begin never entered the encoder.");

            AssertConcurrentCallsRejected(
                () => mux.BeginVideoAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "second video phase begin");
            AssertConcurrentCallsRejected(
                () => mux.WriteAudioChunkAsync(chunk, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "audio while video phase begins");
            AssertConcurrentCallsRejected(
                () => mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "frame before video phase begins");
            AssertConcurrentCallsRejected(
                () => mux.CompleteAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "completion before video phase begins");
            if (encoder.BeginVideoCalls != 1 || encoder.AudioChunkCalls != 0 ||
                encoder.VideoFrameCalls != 0 || encoder.CompleteCalls != 0)
                throw new InvalidOperationException(
                    "Concurrent calls overtook the pending video phase begin.");
        }
        finally
        {
            releaseVideo.Set();
            videoBegin.GetAwaiter().GetResult();
        }

        mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
            .GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.BeginCalls != 1 || encoder.BeginVideoCalls != 1 ||
            encoder.VideoFrameCalls != 1 || encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux did not recover after rejecting concurrent phase starts.");
    }

    private static void VerifyConcurrentWritesRejectOverlap()
    {
        // A second caller on another thread must not overtake an encoder write
        // while the first caller is blocked inside the encoder callback.
        var startInfo = new VideoEncoderStartInfo(
            "concurrent-write-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);

        using var audioEntered = new ManualResetEventSlim();
        using var releaseAudio = new ManualResetEventSlim();
        var encoder = new RecordingVideoEncoder
        {
            OnWriteAudio = () =>
            {
                audioEntered.Set();
                if (!releaseAudio.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Concurrent audio test did not release the encoder.");
            }
        };
        var mux = new VideoExportMuxPipeline(encoder);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();

        Task audioWrite = Task.Run(async () =>
            await mux.WriteAudioChunkAsync(chunk, CancellationToken.None));
        try
        {
            if (!audioEntered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent audio write never entered the encoder.");

            AssertConcurrentCallsRejected(
                () => mux.WriteAudioChunkAsync(chunk, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "second audio write");
            AssertConcurrentCallsRejected(
                () => mux.BeginVideoAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "video phase while audio is pending");
            if (encoder.AudioChunkCalls != 1 || encoder.BeginVideoCalls != 0)
                throw new InvalidOperationException("Concurrent audio operations reached the encoder.");
        }
        finally
        {
            releaseAudio.Set();
            audioWrite.GetAwaiter().GetResult();
        }

        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var videoEntered = new ManualResetEventSlim();
        using var releaseVideo = new ManualResetEventSlim();
        // Use a fresh mux so the video encoder callback can be held independently.
        var videoEncoder = new RecordingVideoEncoder
        {
            OnWriteVideo = () =>
            {
                videoEntered.Set();
                if (!releaseVideo.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Concurrent video test did not release the encoder.");
            }
        };
        var videoMux = new VideoExportMuxPipeline(videoEncoder);
        videoMux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        videoMux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        Task videoWrite = Task.Run(async () =>
            await videoMux.WriteVideoFrameAsync(new byte[12], CancellationToken.None));
        try
        {
            if (!videoEntered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent video write never entered the encoder.");

            AssertConcurrentCallsRejected(
                () => videoMux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "second video frame");
            AssertConcurrentCallsRejected(
                () => videoMux.CompleteAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "completion while video is pending");
            if (videoEncoder.VideoFrameCalls != 1 || videoEncoder.CompleteCalls != 0)
                throw new InvalidOperationException("Concurrent video operations reached the encoder.");
        }
        finally
        {
            releaseVideo.Set();
            videoWrite.GetAwaiter().GetResult();
        }

        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        videoMux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.CompleteCalls != 1 || videoEncoder.CompleteCalls != 1)
            throw new InvalidOperationException("Mux could not complete after concurrent calls were rejected.");
    }

    private static void AssertConcurrentCallsRejected(Action call, string operation)
    {
        Task[] contenders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            bool rejected = false;
            try
            {
                call();
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            if (!rejected)
                throw new InvalidOperationException("Mux accepted concurrent " + operation + ".");
        })).ToArray();

        if (!Task.WaitAll(contenders, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Concurrent mux test timed out: " + operation);
    }

    private static void AssertMuxTerminal(Action call, string operation)
    {
        try
        {
            call();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("cannot be reused", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            "Mux reused an encoder after failed or canceled " + operation + ".");
    }

    private static void VerifyCompletionRequiresVideoPhase()
    {
        // The mux must reject premature completion itself, even if the
        // injected encoder would otherwise accept it.
        var encoder = new RecordingVideoEncoder();
        var mux = new VideoExportMuxPipeline(encoder);
        var startInfo = new VideoEncoderStartInfo(
            "phase-check.mp4", 2, 2, 30, 44100, 2);

        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();

        bool rejected = false;
        try
        {
            mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("video phase", StringComparison.OrdinalIgnoreCase))
        {
            rejected = true;
        }

        if (!rejected || encoder.CompleteCalls != 0)
            throw new InvalidOperationException(
                "Mux completion before BeginVideoAsync must fail without calling the encoder.");

        // A rejected early completion must leave the mux usable.
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux completion must be successful and idempotent after BeginVideoAsync.");
    }

    private static void VerifyCancellationDuringPhaseTransitions()
    {
        var startInfo = new VideoEncoderStartInfo(
            "cancellation-check.mp4", 2, 2, 30, 44100, 2);

        // The injected encoder ignores cancellation and returns successfully.
        // The mux must still observe a cancellation raised during BeginAsync.
        using var beginCancellation = new CancellationTokenSource();
        var beginEncoder = new RecordingVideoEncoder
        {
            OnBegin = () => beginCancellation.Cancel()
        };
        var beginMux = new VideoExportMuxPipeline(beginEncoder);
        bool beginCanceled = false;
        try
        {
            beginMux.BeginAsync(startInfo, beginCancellation.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
            when (ex.CancellationToken == beginCancellation.Token)
        {
            beginCanceled = true;
        }
        if (!beginCanceled)
            throw new InvalidOperationException(
                "Mux BeginAsync ignored cancellation raised by the encoder.");

        bool remainedUnbegun = false;
        try
        {
            beginMux.BeginVideoAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            remainedUnbegun = true;
        }
        if (!remainedUnbegun)
            throw new InvalidOperationException(
                "Canceled mux BeginAsync incorrectly published a begun state.");
        AssertMuxTerminal(
            () => beginMux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult(),
            "encoder start");
        if (beginEncoder.BeginCalls != 1)
            throw new InvalidOperationException("Canceled mux started the encoder twice.");

        // Cancellation after the encoder starts its video phase must not
        // allow a subsequent frame write or successful completion.
        using var videoCancellation = new CancellationTokenSource();
        var videoEncoder = new RecordingVideoEncoder
        {
            OnBeginVideo = () => videoCancellation.Cancel()
        };
        var videoMux = new VideoExportMuxPipeline(videoEncoder);
        videoMux.BeginAsync(startInfo, CancellationToken.None)
            .GetAwaiter().GetResult();

        bool videoCanceled = false;
        try
        {
            videoMux.BeginVideoAsync(videoCancellation.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
            when (ex.CancellationToken == videoCancellation.Token)
        {
            videoCanceled = true;
        }
        if (!videoCanceled)
            throw new InvalidOperationException(
                "Mux BeginVideoAsync ignored cancellation raised by the encoder.");

        bool frameRejected = false;
        try
        {
            videoMux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            frameRejected = true;
        }
        if (!frameRejected || videoEncoder.VideoFrameCalls != 0)
            throw new InvalidOperationException(
                "Canceled video-phase start allowed frame writing.");

        bool completionRejected = false;
        try
        {
            videoMux.CompleteAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            completionRejected = true;
        }
        if (!completionRejected || videoEncoder.CompleteCalls != 0)
            throw new InvalidOperationException(
                "Canceled video-phase start allowed premature completion.");
        var audioChunk = new ExtremeEditor.Audio.OfflineAudioChunk(0, 44100, 2, new float[2]);
        AssertMuxTerminal(
            () => videoMux.WriteAudioChunkAsync(audioChunk, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "video-phase start");
        AssertMuxTerminal(
            () => videoMux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult(),
            "video-phase start retry");
        if (videoEncoder.AudioChunkCalls != 0 || videoEncoder.BeginVideoCalls != 1)
            throw new InvalidOperationException("Canceled video-phase start reused the encoder.");
    }

    private static void VerifyCancellationDuringCompletion()
    {
        // The lower encoder can ignore cancellation and return successfully.
        // The mux must not claim success if cancellation arrived during completion.
        using var cancellation = new CancellationTokenSource();
        var encoder = new RecordingVideoEncoder
        {
            OnComplete = () => cancellation.Cancel()
        };
        var mux = new VideoExportMuxPipeline(encoder);
        var startInfo = new VideoEncoderStartInfo(
            "completion-cancellation-check.mp4", 2, 2, 30, 44100, 2);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();

        bool canceled = false;
        try
        {
            mux.CompleteAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
            canceled = true;
        }
        if (!canceled || encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux ignored cancellation raised while the encoder completed.");
        AssertMuxTerminal(
            () => mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                .GetAwaiter().GetResult(),
            "completion");
        AssertMuxTerminal(
            () => mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult(),
            "completion retry");
        if (encoder.VideoFrameCalls != 0 || encoder.CompleteCalls != 1)
            throw new InvalidOperationException("Canceled completion reused the encoder.");
    }

    private static void VerifyCompletionRejectsReentrantWrites()
    {
        // The encoder can invoke callbacks while its completion is pending.
        // Neither a second completion nor a late frame may reach it.
        VideoExportMuxPipeline? mux = null;
        bool writeRejected = false;
        bool completionRejected = false;
        var encoder = new RecordingVideoEncoder
        {
            OnComplete = () =>
            {
                try
                {
                    mux!.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    writeRejected = true;
                }

                try
                {
                    mux!.CompleteAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    completionRejected = true;
                }
            }
        };
        mux = new VideoExportMuxPipeline(encoder);
        var startInfo = new VideoEncoderStartInfo(
            "reentrant-completion-check.mp4", 2, 2, 30, 44100, 2);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (!writeRejected || !completionRejected ||
            encoder.VideoFrameCalls != 0 || encoder.CompleteCalls != 1)
        {
            throw new InvalidOperationException(
                "Mux allowed a frame or a second completion while completing.");
        }
    }

    private static void VerifyConcurrentCompletionRejectsOverlap()
    {
        // Completion may be suspended on a worker thread while the caller
        // continues to process UI events. Reject a second completion and any
        // late frame until the original encoder completion has finished.
        var startInfo = new VideoEncoderStartInfo(
            "concurrent-completion-check.mp4", 2, 2, 30, 44100, 2);
        using var completionEntered = new ManualResetEventSlim();
        using var releaseCompletion = new ManualResetEventSlim();
        var encoder = new RecordingVideoEncoder
        {
            OnComplete = () =>
            {
                completionEntered.Set();
                if (!releaseCompletion.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Concurrent completion test did not release the encoder.");
            }
        };
        var mux = new VideoExportMuxPipeline(encoder);
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();

        Task completion = Task.Run(async () =>
            await mux.CompleteAsync(CancellationToken.None));
        try
        {
            if (!completionEntered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent completion never entered the encoder.");

            AssertConcurrentCallsRejected(
                () => mux.CompleteAsync(CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "second completion while the encoder is completing");
            AssertConcurrentCallsRejected(
                () => mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "video frame while the encoder is completing");
            if (encoder.CompleteCalls != 1 || encoder.VideoFrameCalls != 0)
                throw new InvalidOperationException(
                    "Concurrent operations overtook the pending encoder completion.");
        }
        finally
        {
            releaseCompletion.Set();
            completion.GetAwaiter().GetResult();
        }

        // Completed mux operations remain idempotent, without restarting ffmpeg.
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.CompleteCalls != 1 || encoder.VideoFrameCalls != 0)
            throw new InvalidOperationException(
                "Mux completion was not idempotent after concurrent calls were rejected.");
    }

    private static void VerifyCancellationDuringChunkWrites()
    {
        // Some encoder implementations complete a write despite cancellation.
        // The mux must surface cancellation before the caller reports progress.
        var startInfo = new VideoEncoderStartInfo(
            "write-cancellation-check.mp4", 2, 2, 30, 44100, 2);
        using var audioCancellation = new CancellationTokenSource();
        var audioEncoder = new RecordingVideoEncoder
        {
            OnWriteAudio = () => audioCancellation.Cancel()
        };
        var audioMux = new VideoExportMuxPipeline(audioEncoder);
        audioMux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        bool audioCanceled = false;
        try
        {
            audioMux.WriteAudioChunkAsync(chunk, audioCancellation.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
            when (ex.CancellationToken == audioCancellation.Token)
        {
            audioCanceled = true;
        }
        if (!audioCanceled || audioEncoder.AudioChunkCalls != 1)
            throw new InvalidOperationException(
                "Mux did not surface cancellation raised during an audio write.");
        AssertMuxTerminal(
            () => audioMux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult(),
            "audio write");
        if (audioEncoder.BeginVideoCalls != 0)
            throw new InvalidOperationException("Canceled audio write advanced the encoder.");

        using var videoCancellation = new CancellationTokenSource();
        var videoEncoder = new RecordingVideoEncoder
        {
            OnWriteVideo = () => videoCancellation.Cancel()
        };
        var videoMux = new VideoExportMuxPipeline(videoEncoder);
        videoMux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
        videoMux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        bool videoCanceled = false;
        try
        {
            videoMux.WriteVideoFrameAsync(new byte[12], videoCancellation.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
            when (ex.CancellationToken == videoCancellation.Token)
        {
            videoCanceled = true;
        }
        if (!videoCanceled || videoEncoder.VideoFrameCalls != 1)
            throw new InvalidOperationException(
                "Mux did not surface cancellation raised during a video write.");
        AssertMuxTerminal(
            () => videoMux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult(),
            "video write");
        if (videoEncoder.CompleteCalls != 0)
            throw new InvalidOperationException("Canceled video write completed the encoder.");
    }


    private static void VerifyEncoderExceptionsAreTerminal()
    {
        // A lower encoder may throw after partially writing data. Every mux
        // operation must then reject reuse without reaching that encoder again.
        var startInfo = new VideoEncoderStartInfo(
            "encoder-fault-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        const string failure = "injected encoder failure";

        foreach (string phase in new[] { "begin", "audio", "video-start", "video-frame", "complete" })
        {
            var encoder = new RecordingVideoEncoder
            {
                OnBegin = phase == "begin" ? () => throw new IOException(failure) : null,
                OnWriteAudio = phase == "audio" ? () => throw new IOException(failure) : null,
                OnBeginVideo = phase == "video-start" ? () => throw new IOException(failure) : null,
                OnWriteVideo = phase == "video-frame" ? () => throw new IOException(failure) : null,
                OnComplete = phase == "complete" ? () => throw new IOException(failure) : null
            };
            var mux = new VideoExportMuxPipeline(encoder);
            bool observed = false;
            try
            {
                mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();
                if (phase == "audio")
                {
                    mux.WriteAudioChunkAsync(chunk, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                else if (phase != "begin")
                {
                    mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (phase == "video-frame")
                        mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                            .GetAwaiter().GetResult();
                    else if (phase == "complete")
                        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (IOException ex) when (ex.Message == failure)
            {
                observed = true;
            }

            if (!observed)
                throw new InvalidOperationException("Encoder fault was not propagated: " + phase);

            int[] counts =
            [
                encoder.BeginCalls, encoder.AudioChunkCalls, encoder.BeginVideoCalls,
                encoder.VideoFrameCalls, encoder.CompleteCalls
            ];
            AssertMuxTerminal(
                () => mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult(),
                phase);
            AssertMuxTerminal(
                () => mux.WriteAudioChunkAsync(chunk, CancellationToken.None)
                    .GetAwaiter().GetResult(), phase);
            AssertMuxTerminal(
                () => mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult(),
                phase);
            AssertMuxTerminal(
                () => mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None)
                    .GetAwaiter().GetResult(), phase);
            AssertMuxTerminal(
                () => mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult(),
                phase);
            int[] after =
            [
                encoder.BeginCalls, encoder.AudioChunkCalls, encoder.BeginVideoCalls,
                encoder.VideoFrameCalls, encoder.CompleteCalls
            ];
            if (!counts.SequenceEqual(after))
                throw new InvalidOperationException("Failed mux reached encoder again: " + phase);
        }
    }

    private static void VerifyPreCanceledOperationsAreRetryable()
    {
        // Cancellation before calling the lower encoder must not poison the mux.
        var startInfo = new VideoEncoderStartInfo(
            "pre-canceled-check.mp4", 2, 2, 30, 44100, 2);
        var chunk = new ExtremeEditor.Audio.OfflineAudioChunk(
            0, 44100, 2, new float[2]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken canceled = cancellation.Token;
        var encoder = new RecordingVideoEncoder();
        var mux = new VideoExportMuxPipeline(encoder);

        static void RequireCancellation(Action operation, string phase)
        {
            try
            {
                operation();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            throw new InvalidOperationException("Pre-canceled mux operation succeeded: " + phase);
        }

        RequireCancellation(
            () => mux.BeginAsync(startInfo, canceled).GetAwaiter().GetResult(), "begin");
        if (encoder.BeginCalls != 0)
            throw new InvalidOperationException("Pre-canceled begin reached encoder.");
        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();

        RequireCancellation(
            () => mux.WriteAudioChunkAsync(chunk, canceled).GetAwaiter().GetResult(), "audio");
        if (encoder.AudioChunkCalls != 0)
            throw new InvalidOperationException("Pre-canceled audio reached encoder.");
        mux.WriteAudioChunkAsync(chunk, CancellationToken.None).GetAwaiter().GetResult();

        RequireCancellation(
            () => mux.BeginVideoAsync(canceled).GetAwaiter().GetResult(), "video start");
        if (encoder.BeginVideoCalls != 0)
            throw new InvalidOperationException("Pre-canceled video start reached encoder.");
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();

        RequireCancellation(
            () => mux.WriteVideoFrameAsync(new byte[12], canceled).GetAwaiter().GetResult(),
            "video frame");
        if (encoder.VideoFrameCalls != 0)
            throw new InvalidOperationException("Pre-canceled frame reached encoder.");
        mux.WriteVideoFrameAsync(new byte[12], CancellationToken.None).GetAwaiter().GetResult();

        RequireCancellation(
            () => mux.CompleteAsync(canceled).GetAwaiter().GetResult(), "completion");
        if (encoder.CompleteCalls != 0)
            throw new InvalidOperationException("Pre-canceled completion reached encoder.");
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (encoder.BeginCalls != 1 || encoder.AudioChunkCalls != 1 ||
            encoder.BeginVideoCalls != 1 || encoder.VideoFrameCalls != 1 ||
            encoder.CompleteCalls != 1)
        {
            throw new InvalidOperationException("Pre-canceled operations disrupted normal export.");
        }
    }

    private sealed class RecordingVideoEncoder : IVideoEncoder
    {
        internal int BeginCalls { get; private set; }
        internal int BeginVideoCalls { get; private set; }
        internal int CompleteCalls { get; private set; }
        internal int VideoFrameCalls { get; private set; }
        internal int AudioChunkCalls { get; private set; }
        internal Action? OnWriteVideo { get; init; }
        internal Action? OnWriteAudio { get; init; }
        internal Action? OnBegin { get; init; }
        internal Action? OnBeginVideo { get; init; }
        internal Action? OnComplete { get; init; }

        public ValueTask BeginAsync(
            VideoEncoderStartInfo startInfo, CancellationToken cancellationToken)
        {
            BeginCalls++;
            OnBegin?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteVideoFrameAsync(
            byte[] rgb, CancellationToken cancellationToken)
        {
            VideoFrameCalls++;
            OnWriteVideo?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAudioChunkAsync(
            ExtremeEditor.Audio.OfflineAudioChunk chunk, CancellationToken cancellationToken)
        {
            AudioChunkCalls++;
            OnWriteAudio?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginVideoAsync(CancellationToken cancellationToken)
        {
            BeginVideoCalls++;
            OnBeginVideo?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCalls++;
            OnComplete?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string pipelinePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportMuxPipeline.cs");
        if (!File.Exists(pipelinePath))
            throw new InvalidOperationException("VE4 mux pipeline source is missing.");

        string source = File.ReadAllText(pipelinePath);
        var failures = new List<string>();

        if (!source.Contains("IVideoEncoder", StringComparison.Ordinal))
            failures.Add("mux pipeline must depend on IVideoEncoder");
        if (!source.Contains("WriteVideoFrameAsync", StringComparison.Ordinal))
            failures.Add("mux pipeline must forward frames incrementally");
        if (!source.Contains("WriteAudioChunkAsync", StringComparison.Ordinal))
            failures.Add("mux pipeline must forward audio chunks incrementally");
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("mux pipeline must observe cancellation");
        if (source.Contains("List<RenderedFrame>", StringComparison.Ordinal) ||
            source.Contains("List<OfflineAudioChunk>", StringComparison.Ordinal))
        {
            failures.Add("mux pipeline must not accumulate full video/audio exports in memory");
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4 mux source contract failed:\n- " +
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
            "Could not locate the ExtremeEditor repository root for VE4 regression.");
    }
}
