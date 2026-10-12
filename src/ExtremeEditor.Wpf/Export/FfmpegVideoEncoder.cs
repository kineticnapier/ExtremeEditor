using System.Buffers;
using System.Diagnostics;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf.Export;

internal sealed class FfmpegVideoEncoder : IVideoEncoder
{
    private readonly string _ffmpegPath;
    private VideoEncoderStartInfo? _startInfo;
    private string? _videoTempPath;
    private string? _audioTempPath;
    private FileStream? _videoStream;
    private FileStream? _audioStream;
    private bool _completed;
    private bool _disposed;

    internal FfmpegVideoEncoder(string ffmpegPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = Path.GetFullPath(ffmpegPath);
    }

    public ValueTask BeginAsync(
        VideoEncoderStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_startInfo is not null)
            throw new InvalidOperationException("The ffmpeg encoder has already begun.");

        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string outputPath = Path.GetFullPath(startInfo.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ExtremeEditor",
            "VideoExport");
        Directory.CreateDirectory(tempDirectory);

        string id = Guid.NewGuid().ToString("N");
        _videoTempPath = Path.Combine(tempDirectory, id + ".rgb24");
        _audioTempPath = Path.Combine(tempDirectory, id + ".f32le");

        try
        {
            _videoStream = new FileStream(
                _videoTempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _audioStream = new FileStream(
                _audioTempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _startInfo = startInfo with { OutputPath = outputPath };
        }
        catch
        {
            DisposeStreams();
            DeleteTemporaryFiles();
            throw;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteVideoFrameAsync(
        byte[] rgb,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        VideoEncoderStartInfo startInfo = RequireWritable();
        ArgumentNullException.ThrowIfNull(rgb);
        cancellationToken.ThrowIfCancellationRequested();

        int expectedBytes = checked(startInfo.Width * startInfo.Height * 3);
        if (rgb.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"RGB24 frame length must be exactly {expectedBytes} bytes; got {rgb.Length}.",
                nameof(rgb));
        }

        await _videoStream!.WriteAsync(rgb, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        VideoEncoderStartInfo startInfo = RequireWritable();
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        if (chunk.SampleRate != startInfo.AudioSampleRate)
        {
            throw new ArgumentException(
                $"Audio sample rate changed during export: expected {startInfo.AudioSampleRate}, got {chunk.SampleRate}.",
                nameof(chunk));
        }
        if (chunk.Channels != startInfo.AudioChannels)
        {
            throw new ArgumentException(
                $"Audio channel count changed during export: expected {startInfo.AudioChannels}, got {chunk.Channels}.",
                nameof(chunk));
        }

        int byteCount = checked(chunk.Samples.Length * sizeof(float));
        byte[] bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, byteCount);
            await _audioStream!
                .WriteAsync(bytes.AsMemory(0, byteCount), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completed)
            return;

        VideoEncoderStartInfo startInfo = _startInfo
            ?? throw new InvalidOperationException("The ffmpeg encoder has not begun.");
        cancellationToken.ThrowIfCancellationRequested();

        await FlushAndCloseStreamsAsync(cancellationToken).ConfigureAwait(false);

        string videoPath = _videoTempPath
            ?? throw new InvalidOperationException("Video temporary path is unavailable.");
        string audioPath = _audioTempPath
            ?? throw new InvalidOperationException("Audio temporary path is unavailable.");

        // ffmpeg input contracts:
        // video: -f rawvideo -pix_fmt rgb24
        // audio: -f f32le
        string arguments =
            "-hide_banner -loglevel error -y " +
            $"-f rawvideo -pix_fmt rgb24 -s:v {startInfo.Width}x{startInfo.Height} " +
            $"-r {startInfo.FramesPerSecond} -i {Quote(videoPath)} " +
            $"-f f32le -ar {startInfo.AudioSampleRate} -ac {startInfo.AudioChannels} " +
            $"-i {Quote(audioPath)} " +
            "-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest " +
            Quote(startInfo.OutputPath);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        try
        {
            using Process process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}: {stderr.Trim()}");
            }

            _completed = true;
        }
        finally
        {
            DeleteTemporaryFiles();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (_videoStream is not null)
                await _videoStream.DisposeAsync().ConfigureAwait(false);
            if (_audioStream is not null)
                await _audioStream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _videoStream = null;
            _audioStream = null;
            DeleteTemporaryFiles();
        }

        GC.SuppressFinalize(this);
    }

    private VideoEncoderStartInfo RequireWritable()
    {
        if (_startInfo is null)
            throw new InvalidOperationException("The ffmpeg encoder has not begun.");
        if (_completed)
            throw new InvalidOperationException("The ffmpeg encoder has already completed.");
        if (_videoStream is null || _audioStream is null)
            throw new InvalidOperationException("The ffmpeg encoder streams are not available.");
        return _startInfo;
    }

    private async ValueTask FlushAndCloseStreamsAsync(CancellationToken cancellationToken)
    {
        FileStream? video = _videoStream;
        FileStream? audio = _audioStream;
        _videoStream = null;
        _audioStream = null;

        if (video is not null)
        {
            await video.FlushAsync(cancellationToken).ConfigureAwait(false);
            await video.DisposeAsync().ConfigureAwait(false);
        }

        if (audio is not null)
        {
            await audio.FlushAsync(cancellationToken).ConfigureAwait(false);
            await audio.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void DisposeStreams()
    {
        _videoStream?.Dispose();
        _audioStream?.Dispose();
        _videoStream = null;
        _audioStream = null;
    }

    private void DeleteTemporaryFiles()
    {
        Delete(_videoTempPath);
        Delete(_audioTempPath);
        _videoTempPath = null;
        _audioTempPath = null;
    }

    private static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup. A process or antivirus may briefly retain the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static string Quote(string value) =>
        """ + value.Replace(""", "\\"", StringComparison.Ordinal) + """;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Cancellation should retain its original exception.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
