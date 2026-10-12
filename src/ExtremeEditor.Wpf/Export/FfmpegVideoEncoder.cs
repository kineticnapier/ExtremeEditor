using System.Buffers;
using System.Diagnostics;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf.Export;

internal sealed class FfmpegVideoEncoder : IVideoEncoder
{
    private readonly string _ffmpegPath;
    private VideoEncoderStartInfo? _startInfo;
    private string? _audioTempPath;
    private FileStream? _audioStream;
    private Process? _ffmpegProcess;
    private Stream? _videoInput;
    private Task<string>? _stderrTask;
    private Task<string>? _stdoutTask;
    private bool _videoBegun;
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
        _audioTempPath = Path.Combine(tempDirectory, id + ".f32le");

        try
        {
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
            DisposeAudioStream();
            DeleteTemporaryFiles();
            throw;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        VideoEncoderStartInfo startInfo = RequireAudioWritable();
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

    public async ValueTask BeginVideoAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_videoBegun)
            throw new InvalidOperationException("The ffmpeg video phase has already begun.");

        VideoEncoderStartInfo startInfo = _startInfo
            ?? throw new InvalidOperationException("The ffmpeg encoder has not begun.");
        cancellationToken.ThrowIfCancellationRequested();

        FileStream? audio = _audioStream;
        _audioStream = null;
        if (audio is not null)
        {
            await audio.FlushAsync(cancellationToken).ConfigureAwait(false);
            await audio.DisposeAsync().ConfigureAwait(false);
        }

        string audioPath = _audioTempPath
            ?? throw new InvalidOperationException("Audio temporary path is unavailable.");

        // video: -f rawvideo -pix_fmt rgb24 -i pipe:0
        // audio: -f f32le
        string arguments =
            "-hide_banner -loglevel error -y " +
            $"-f rawvideo -pix_fmt rgb24 -s:v {startInfo.Width}x{startInfo.Height} " +
            $"-r {startInfo.FramesPerSecond} -i pipe:0 " +
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
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        try
        {
            Process process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            _ffmpegProcess = process;
            _videoInput = process.StandardInput.BaseStream;
            _stderrTask = process.StandardError.ReadToEndAsync();
            _stdoutTask = process.StandardOutput.ReadToEndAsync();
            _videoBegun = true;
        }
        catch
        {
            DisposeProcessState();
            throw;
        }
    }

    public async ValueTask WriteVideoFrameAsync(
        byte[] rgb,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        VideoEncoderStartInfo startInfo = RequireVideoWritable();
        ArgumentNullException.ThrowIfNull(rgb);
        cancellationToken.ThrowIfCancellationRequested();

        int expectedBytes = checked(startInfo.Width * startInfo.Height * 3);
        if (rgb.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"RGB24 frame length must be exactly {expectedBytes} bytes; got {rgb.Length}.",
                nameof(rgb));
        }

        try
        {
            await _videoInput!
                .WriteAsync(rgb, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException) when (_ffmpegProcess is { HasExited: true } process)
        {
            string stderr = _stderrTask is null
                ? string.Empty
                : await _stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}: {stderr.Trim()}");
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completed)
            return;
        if (!_videoBegun)
            throw new InvalidOperationException("The ffmpeg video phase has not begun.");

        cancellationToken.ThrowIfCancellationRequested();

        Process process = _ffmpegProcess
            ?? throw new InvalidOperationException("ffmpeg process is unavailable.");

        try
        {
            if (_videoInput is not null)
            {
                await _videoInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _videoInput.DisposeAsync().ConfigureAwait(false);
                _videoInput = null;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string stderr = _stderrTask is null
                ? string.Empty
                : await _stderrTask.ConfigureAwait(false);
            if (_stdoutTask is not null)
                _ = await _stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}: {stderr.Trim()}");
            }

            _completed = true;
        }
        finally
        {
            DisposeProcessState();
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
            if (_videoInput is not null)
                await _videoInput.DisposeAsync().ConfigureAwait(false);
            if (_audioStream is not null)
                await _audioStream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _videoInput = null;
            _audioStream = null;
            if (_ffmpegProcess is not null)
                TryKill(_ffmpegProcess);
            DisposeProcessState();
            DeleteTemporaryFiles();
        }

        GC.SuppressFinalize(this);
    }

    private VideoEncoderStartInfo RequireAudioWritable()
    {
        if (_startInfo is null)
            throw new InvalidOperationException("The ffmpeg encoder has not begun.");
        if (_videoBegun)
            throw new InvalidOperationException("The ffmpeg audio phase has already ended.");
        if (_completed)
            throw new InvalidOperationException("The ffmpeg encoder has already completed.");
        if (_audioStream is null)
            throw new InvalidOperationException("The ffmpeg audio stream is not available.");
        return _startInfo;
    }

    private VideoEncoderStartInfo RequireVideoWritable()
    {
        if (_startInfo is null)
            throw new InvalidOperationException("The ffmpeg encoder has not begun.");
        if (!_videoBegun)
            throw new InvalidOperationException("The ffmpeg video phase has not begun.");
        if (_completed)
            throw new InvalidOperationException("The ffmpeg encoder has already completed.");
        if (_videoInput is null)
            throw new InvalidOperationException("The ffmpeg video input stream is not available.");
        return _startInfo;
    }

    private void DisposeAudioStream()
    {
        _audioStream?.Dispose();
        _audioStream = null;
    }

    private void DisposeProcessState()
    {
        _videoInput?.Dispose();
        _videoInput = null;
        _ffmpegProcess?.Dispose();
        _ffmpegProcess = null;
        _stderrTask = null;
        _stdoutTask = null;
    }

    private void DeleteTemporaryFiles()
    {
        Delete(_audioTempPath);
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
        """ + value.Replace(""", "\"", StringComparison.Ordinal) + """;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Cancellation/disposal should retain its original exception.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
