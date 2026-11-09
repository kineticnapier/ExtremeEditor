using System.IO;
using System.Reflection;
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
        VerifyConcurrentWritesRejectOverlap();
        VerifyCancellationDuringPhaseTransitions();
        VerifyCancellationDuringCompletion();
        VerifyCompletionRejectsReentrantWrites();
        VerifyCancellationDuringChunkWrites();
        VerifySourceContract();
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
