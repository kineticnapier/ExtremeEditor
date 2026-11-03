using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportFfmpegEncoderRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type? encoderContract = assembly.GetType("ExtremeEditor.Wpf.Export.IVideoEncoder");
        Type? ffmpegType = assembly.GetType("ExtremeEditor.Wpf.Export.FfmpegVideoEncoder");

        if (encoderContract is null)
            throw new InvalidOperationException("VE4 IVideoEncoder is missing.");
        if (ffmpegType is null)
            throw new InvalidOperationException("VE4b FfmpegVideoEncoder is missing.");
        if (!encoderContract.IsAssignableFrom(ffmpegType))
            throw new InvalidOperationException("FfmpegVideoEncoder must implement IVideoEncoder.");

        ConstructorInfo? constructor = ffmpegType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                "FfmpegVideoEncoder must accept an ffmpeg executable path.");
        }

        VerifyYuv420pDimensionValidation(ffmpegType);
        VerifyAtomicCommit(ffmpegType, constructor);
        VerifyAudioFlushFailureDisposesStream(ffmpegType, constructor, throwOnDispose: false);
        VerifyAudioFlushFailureDisposesStream(ffmpegType, constructor, throwOnDispose: true);
        VerifyCancellationDuringAudioFlushPreventsProcessStart(ffmpegType, constructor);
        VerifyVideoDisposeFailureStillDisposesAudioStream(ffmpegType, constructor, throwOnAudioDispose: false);
        VerifyVideoDisposeFailureStillDisposesAudioStream(ffmpegType, constructor, throwOnAudioDispose: true);
        VerifyFailedVideoCompletionTerminatesProcess(ffmpegType, constructor);
        VerifyDisposeWaitsForChildExitAndCleansStaging(ffmpegType, constructor);
        VerifySourceContract();
    }

    private static void VerifyYuv420pDimensionValidation(Type encoderType)
    {
        Type startInfoType = encoderType.Assembly.GetType("ExtremeEditor.Wpf.Export.VideoEncoderStartInfo")
            ?? throw new InvalidOperationException("VideoEncoderStartInfo is missing.");
        ConstructorInfo constructor = startInfoType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 6);
        MethodInfo validate = startInfoType.GetMethod(
                "Validate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VideoEncoderStartInfo.Validate is missing.");

        (int Width, int Height, string? InvalidParameter)[] cases =
        [
            (2, 2, null),
            (1920, 1080, null),
            (8192, 8192, null), // Large but representable RGB24 buffer; no allocation.
            (32768, 32768, "Width"), // 3 GiB RGB24 frame cannot fit byte[].
            (2, int.MaxValue - 1, "Width"), // Reject oversized height before ffmpeg.
            (3, 2, "Width"),
            (2, 3, "Height"),
            (0, 2, "Width"),
            (2, 0, "Height")
        ];
        foreach (var (width, height, invalidParameter) in cases)
        {
            object info = constructor.Invoke(["test.mp4", width, height, 30, 44100, 2]);
            if (invalidParameter is null)
            {
                validate.Invoke(info, null);
                continue;
            }

            try
            {
                validate.Invoke(info, null);
                throw new InvalidOperationException(
                    $"H.264 dimensions {width}x{height} were unexpectedly accepted.");
            }
            catch (TargetInvocationException ex) when (
                ex.InnerException is ArgumentOutOfRangeException argument &&
                argument.ParamName == invalidParameter)
            {
                // Invalid dimensions must fail before ffmpeg is started.
            }
        }
    }

    private static void VerifyAtomicCommit(Type encoderType, ConstructorInfo constructor)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-VideoCommit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string final = Path.Combine(directory, "result.mp4");
            string temporary = Path.Combine(directory, ".result.tmp.mp4");
            File.WriteAllText(final, "old-complete-video");

            MethodInfo commit = encoderType.GetMethod(
                "CommitVideoOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Atomic video commit helper is missing.");
            FieldInfo tempField = encoderType.GetField("_videoTempPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo finalField = encoderType.GetField("_finalOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
            tempField.SetValue(encoder, temporary);
            finalField.SetValue(encoder, final);

            try
            {
                commit.Invoke(encoder, [CancellationToken.None]);
                throw new InvalidOperationException("Missing temporary video unexpectedly replaced the destination.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
            }
            if (File.ReadAllText(final) != "old-complete-video")
                throw new InvalidOperationException("Failed video commit damaged the existing destination.");

            File.WriteAllText(temporary, "new-complete-video");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                commit.Invoke(encoder, [canceled.Token]);
                throw new InvalidOperationException("Canceled video commit unexpectedly replaced the destination.");
            }
            catch (TargetInvocationException ex) when (
                ex.InnerException is OperationCanceledException cancellation &&
                cancellation.CancellationToken == canceled.Token)
            {
            }
            if (File.ReadAllText(final) != "old-complete-video" || !File.Exists(temporary))
                throw new InvalidOperationException("Canceled video commit modified the destination or removed staging.");

            commit.Invoke(encoder, [CancellationToken.None]);
            if (File.ReadAllText(final) != "new-complete-video" || File.Exists(temporary))
                throw new InvalidOperationException("Successful video commit was not an atomic replacement.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyAudioFlushFailureDisposesStream(
        Type encoderType, ConstructorInfo constructor, bool throwOnDispose)
    {
        // No ffmpeg executable is needed: the injected audio flush fails before process startup.
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-AudioFlush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
        ThrowingAudioFlushStream? stream = null;
        try
        {
            Type startInfoType = encoderType.Assembly.GetType("ExtremeEditor.Wpf.Export.VideoEncoderStartInfo")
                ?? throw new InvalidOperationException("VideoEncoderStartInfo is missing.");
            ConstructorInfo startConstructor = startInfoType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(ctor => ctor.GetParameters().Length == 6);
            object startInfo = startConstructor.Invoke([
                Path.Combine(directory, "output.mp4"), 2, 2, 30, 44100, 2
            ]);
            FieldInfo startField = encoderType.GetField("_startInfo", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder start-info field is missing.");
            FieldInfo audioField = encoderType.GetField("_audioStream", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder audio stream field is missing.");
            MethodInfo beginVideo = encoderType.GetMethod("BeginVideoAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("Encoder video-phase entry is missing.");

            stream = new ThrowingAudioFlushStream(
                Path.Combine(directory, "audio.f32le"), throwOnDispose);
            startField.SetValue(encoder, startInfo);
            audioField.SetValue(encoder, stream);
            try
            {
                object? result = beginVideo.Invoke(encoder, [CancellationToken.None]);
                if (result is not ValueTask task)
                    throw new InvalidOperationException("BeginVideoAsync must return ValueTask.");
                task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Injected audio flush unexpectedly succeeded.");
            }
            catch (IOException ex) when (ex.Message == "injected audio flush failure")
            {
                // Preserve the original flush failure, while releasing the detached stream.
            }

            if (!stream.DisposedAsynchronously || audioField.GetValue(encoder) is not null)
                throw new InvalidOperationException("Failed audio flush leaked the detached file stream.");
        }
        finally
        {
            ((IAsyncDisposable)encoder).DisposeAsync().AsTask().GetAwaiter().GetResult();
            stream?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyCancellationDuringAudioFlushPreventsProcessStart(
        Type encoderType, ConstructorInfo constructor)
    {
        // The stream deliberately ignores the cancellation token while flushing.
        // The encoder must observe cancellation before it starts a subprocess.
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-AudioCancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
        using var cancellation = new CancellationTokenSource();
        CancelDuringAudioFlushStream? stream = null;
        try
        {
            Type startInfoType = encoderType.Assembly.GetType("ExtremeEditor.Wpf.Export.VideoEncoderStartInfo")
                ?? throw new InvalidOperationException("VideoEncoderStartInfo is missing.");
            ConstructorInfo startConstructor = startInfoType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(ctor => ctor.GetParameters().Length == 6);
            object startInfo = startConstructor.Invoke([
                Path.Combine(directory, "output.mp4"), 2, 2, 30, 44100, 2
            ]);
            FieldInfo startField = encoderType.GetField("_startInfo", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder start-info field is missing.");
            FieldInfo audioField = encoderType.GetField("_audioStream", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder audio-stream field is missing.");
            FieldInfo begunField = encoderType.GetField("_videoBegun", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder video-phase field is missing.");
            FieldInfo processField = encoderType.GetField("_ffmpegProcess", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder process field is missing.");
            MethodInfo beginVideo = encoderType.GetMethod("BeginVideoAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("Encoder video-phase entry is missing.");

            stream = new CancelDuringAudioFlushStream(
                Path.Combine(directory, "audio.f32le"), cancellation);
            startField.SetValue(encoder, startInfo);
            audioField.SetValue(encoder, stream);

            try
            {
                object? result = beginVideo.Invoke(encoder, [cancellation.Token]);
                if (result is not ValueTask task)
                    throw new InvalidOperationException("BeginVideoAsync must return ValueTask.");
                task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Canceled audio flush unexpectedly started video.");
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
            {
                // The cancellation after a successful flush must be propagated.
            }

            if (!stream.DisposedAsynchronously || audioField.GetValue(encoder) is not null)
                throw new InvalidOperationException("Canceled audio flush leaked its stream.");
            if (begunField.GetValue(encoder) is not false || processField.GetValue(encoder) is not null)
                throw new InvalidOperationException("Canceled audio flush started or retained ffmpeg.");
        }
        finally
        {
            ((IAsyncDisposable)encoder).DisposeAsync().AsTask().GetAwaiter().GetResult();
            stream?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancelDuringAudioFlushStream(
        string path, CancellationTokenSource cancellation)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        internal bool DisposedAsynchronously { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            DisposedAsynchronously = true;
            return base.DisposeAsync();
        }
    }

    private sealed class ThrowingAudioFlushStream(string path, bool throwOnDispose = false)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        internal bool DisposedAsynchronously { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("injected audio flush failure"));

        public override async ValueTask DisposeAsync()
        {
            DisposedAsynchronously = true;
            await base.DisposeAsync().ConfigureAwait(false);
            if (throwOnDispose)
                throw new IOException("injected audio dispose failure");
        }
    }

    private static void VerifyVideoDisposeFailureStillDisposesAudioStream(
        Type encoderType, ConstructorInfo constructor, bool throwOnAudioDispose)
    {
        // Preserve the video failure even if closing the audio file also fails.
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-VideoDispose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
        ThrowingAudioFlushStream? audio = null;
        try
        {
            FieldInfo inputField = encoderType.GetField(
                "_videoInput", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder video-input field is missing.");
            FieldInfo audioField = encoderType.GetField(
                "_audioStream", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder audio-stream field is missing.");

            audio = new ThrowingAudioFlushStream(
                Path.Combine(directory, "audio.f32le"), throwOnDispose: throwOnAudioDispose);
            inputField.SetValue(encoder, new ThrowingVideoDisposeStream());
            audioField.SetValue(encoder, audio);

            try
            {
                ((IAsyncDisposable)encoder).DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException("Injected video disposal unexpectedly succeeded.");
            }
            catch (IOException ex) when (ex.Message == "injected video dispose failure")
            {
                // The first failure must win whether audio disposal succeeds or fails.
            }

            if (!audio.DisposedAsynchronously)
                throw new InvalidOperationException("Failed video disposal skipped audio stream disposal.");
            if (inputField.GetValue(encoder) is not null || audioField.GetValue(encoder) is not null)
                throw new InvalidOperationException("Failed video disposal retained encoder stream state.");
        }
        finally
        {
            audio?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowingVideoDisposeStream : MemoryStream
    {
        public override ValueTask DisposeAsync() =>
            ValueTask.FromException(new IOException("injected video dispose failure"));
    }

    private static void VerifyFailedVideoCompletionTerminatesProcess(
        Type encoderType, ConstructorInfo constructor)
    {
        // Windows CI runs this behavioral test without requiring an ffmpeg binary.
        // A child that is still running when the pipe flush fails must be killed.
        if (!OperatingSystem.IsWindows())
            return;

        // Check both a failed flush alone and a flush whose cleanup also fails.
        VerifyFailedVideoCompletionCase(encoderType, constructor, throwOnDispose: false);
        VerifyFailedVideoCompletionCase(encoderType, constructor, throwOnDispose: true);
    }

    private static void VerifyFailedVideoCompletionCase(
        Type encoderType, ConstructorInfo constructor, bool throwOnDispose)
    {
        FieldInfo processField = encoderType.GetField(
            "_ffmpegProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Encoder process field is missing.");
        FieldInfo inputField = encoderType.GetField(
            "_videoInput", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Encoder video-input field is missing.");
        FieldInfo begunField = encoderType.GetField(
            "_videoBegun", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Encoder video-phase field is missing.");
        MethodInfo complete = encoderType.GetMethod(
            "CompleteAsync", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Encoder completion method is missing.");

        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c ping -n 30 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start video cleanup test child.");
        using Process observer = Process.GetProcessById(child.Id);
        object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
        try
        {
            if (observer.HasExited)
                throw new InvalidOperationException("Video cleanup test child exited too early.");

            begunField.SetValue(encoder, true);
            processField.SetValue(encoder, child);
            var failingStream = new ThrowingVideoFlushStream(throwOnDispose);
            inputField.SetValue(encoder, failingStream);
            try
            {
                object? result = complete.Invoke(encoder, [CancellationToken.None]);
                if (result is not ValueTask task)
                    throw new InvalidOperationException("CompleteAsync must return ValueTask.");
                task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Injected video flush unexpectedly succeeded.");
            }
            catch (IOException ex) when (ex.Message == "injected video flush failure")
            {
                // Completion must preserve the original exception and stop ffmpeg.
            }

            if (!failingStream.DisposedAsynchronously)
                throw new InvalidOperationException(
                    "Failed video flush skipped asynchronous video pipe disposal.");
            if (!observer.WaitForExit(5000))
                throw new InvalidOperationException("Failed video completion left ffmpeg running.");
            if (processField.GetValue(encoder) is not null ||
                inputField.GetValue(encoder) is not null)
            {
                throw new InvalidOperationException("Failed video completion retained process state.");
            }
        }
        finally
        {
            if (!observer.HasExited)
            {
                observer.Kill(entireProcessTree: true);
                observer.WaitForExit(5000);
            }
            ((IAsyncDisposable)encoder).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Cleanup must wait for the killed process to release file handles before
    // attempting to remove temporary media. No real ffmpeg binary is needed.
    private static void VerifyDisposeWaitsForChildExitAndCleansStaging(
        Type encoderType, ConstructorInfo constructor)
    {
        if (!OperatingSystem.IsWindows())
            return;

        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-VideoCleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string audio = Path.Combine(directory, "audio.f32le");
        string video = Path.Combine(directory, "video.tmp.mp4");
        File.WriteAllText(audio, "temporary audio");
        File.WriteAllText(video, "temporary video");

        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c ping -n 30 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start video cleanup test child.");
        using Process observer = Process.GetProcessById(child.Id);
        object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
        try
        {
            if (observer.HasExited)
                throw new InvalidOperationException("Video cleanup test child exited too early.");

            FieldInfo processField = encoderType.GetField(
                "_ffmpegProcess", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder process field is missing.");
            FieldInfo audioPathField = encoderType.GetField(
                "_audioTempPath", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder audio staging path is missing.");
            FieldInfo videoPathField = encoderType.GetField(
                "_videoTempPath", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Encoder video staging path is missing.");

            processField.SetValue(encoder, child);
            audioPathField.SetValue(encoder, audio);
            videoPathField.SetValue(encoder, video);
            ((IAsyncDisposable)encoder).DisposeAsync().AsTask().GetAwaiter().GetResult();

            if (!observer.HasExited)
                throw new InvalidOperationException(
                    "Encoder disposal returned before its child process exited.");
            if (File.Exists(audio) || File.Exists(video))
                throw new InvalidOperationException(
                    "Encoder disposal left temporary media after process exit.");
        }
        finally
        {
            if (!observer.HasExited)
            {
                observer.Kill(entireProcessTree: true);
                observer.WaitForExit(5000);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowingVideoFlushStream(bool throwOnDispose = false) : MemoryStream
    {
        public bool DisposedAsynchronously { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("injected video flush failure"));

        public override async ValueTask DisposeAsync()
        {
            DisposedAsynchronously = true;
            await base.DisposeAsync().ConfigureAwait(false);
            if (throwOnDispose)
                throw new IOException("injected video dispose failure");
        }
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "FfmpegVideoEncoder.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException("VE4b FfmpegVideoEncoder source is missing.");

        string source = File.ReadAllText(path);
        var failures = new List<string>();

        if (!source.Contains("ProcessStartInfo", StringComparison.Ordinal))
            failures.Add("encoder must invoke ffmpeg as a subprocess");
        if (!source.Contains("-f rawvideo", StringComparison.Ordinal))
            failures.Add("encoder must describe RGB frames as rawvideo");
        if (!source.Contains("rgb24", StringComparison.Ordinal))
            failures.Add("encoder must use RGB24 for RenderedFrame RGB bytes");
        if (!source.Contains("f32le", StringComparison.Ordinal))
            failures.Add("encoder must describe offline PCM as 32-bit float little-endian");
        if (!source.Contains("libx264", StringComparison.Ordinal))
            failures.Add("encoder must encode H.264 video");
        if (!source.Contains("aac", StringComparison.Ordinal))
            failures.Add("encoder must encode AAC audio");
        if (!source.Contains("FileStream", StringComparison.Ordinal))
            failures.Add("encoder must stream raw media to bounded temp files instead of accumulating it in memory");
        if (!source.Contains("Delete", StringComparison.Ordinal))
            failures.Add("encoder must clean up temporary raw media files");
        if (!source.Contains("_videoTempPath", StringComparison.Ordinal) ||
            !source.Contains("File.Move", StringComparison.Ordinal))
        {
            failures.Add("encoder must commit a same-directory temporary MP4 only after success");
        }
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("encoder must observe cancellation");
        if (!source.Contains("CommitVideoOutput(cancellationToken)", StringComparison.Ordinal))
            failures.Add("final MP4 commit must receive the active cancellation token");
        if (!source.Contains("TryKill(_ffmpegProcess)", StringComparison.Ordinal) ||
            !source.Contains("TryKill(process)", StringComparison.Ordinal))
        {
            failures.Add("ffmpeg must be terminated when startup or completion fails");
        }
        if (source.Contains("List<byte[]>", StringComparison.Ordinal) ||
            source.Contains("List<float>", StringComparison.Ordinal))
        {
            failures.Add("encoder must not accumulate complete media in memory");
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4b ffmpeg source contract failed:\n- " +
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
            "Could not locate the ExtremeEditor repository root for VE4b regression.");
    }
}
