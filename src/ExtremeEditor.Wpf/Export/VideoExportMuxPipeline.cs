using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf.Export;

internal sealed class VideoExportMuxPipeline
{
    private readonly IVideoEncoder _encoder;
    // A mux is normally used by one caller, but callbacks and asynchronous
    // continuations may run on other threads. Guard each state check and
    // transition atomically; never hold the lock while invoking the encoder.
    private readonly object _stateGate = new();
    private bool _begun;
    private bool _beginning;
    private bool _videoBegun;
    private bool _videoBeginning;
    private bool _audioWriting;
    private bool _videoWriting;
    private bool _completed;
    private bool _completing;

    internal VideoExportMuxPipeline(IVideoEncoder encoder)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
    }

    internal async ValueTask BeginAsync(
        VideoEncoderStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_begun || _beginning)
                throw new InvalidOperationException("Video export encoder has already begun or is starting.");
            if (_completed)
                throw new InvalidOperationException("Video export encoder has already completed.");

            ArgumentNullException.ThrowIfNull(startInfo);
            startInfo.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            _beginning = true;
        }

        bool succeeded = false;
        try
        {
            await _encoder.BeginAsync(startInfo, cancellationToken).ConfigureAwait(false);
            // A custom encoder may finish BeginAsync despite cancellation. Do not
            // publish a usable mux state after cancellation; the caller disposes it.
            cancellationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        finally
        {
            lock (_stateGate)
            {
                if (succeeded)
                    _begun = true;
                _beginning = false;
            }
        }
    }

    internal async ValueTask WriteVideoFrameAsync(
        byte[] rgb,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            EnsureVideoWritable();
            ArgumentNullException.ThrowIfNull(rgb);
            cancellationToken.ThrowIfCancellationRequested();
            _videoWriting = true;
        }

        try
        {
            await _encoder.WriteVideoFrameAsync(rgb, cancellationToken).ConfigureAwait(false);
            // An encoder may finish a write despite cancellation. Do not report
            // that the canceled frame was accepted successfully.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_stateGate)
                _videoWriting = false;
        }
    }

    internal async ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            EnsureAudioWritable();
            ArgumentNullException.ThrowIfNull(chunk);
            cancellationToken.ThrowIfCancellationRequested();
            _audioWriting = true;
        }

        try
        {
            await _encoder.WriteAudioChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            // Observe cancellation raised during a non-cooperative audio write.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_stateGate)
                _audioWriting = false;
        }
    }

    internal async ValueTask BeginVideoAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (!_begun)
                throw new InvalidOperationException("Video export encoder has not begun.");
            if (_videoBegun || _videoBeginning)
                throw new InvalidOperationException("Video export video phase has already begun or is starting.");
            if (_audioWriting)
                throw new InvalidOperationException("Video export cannot begin its video phase while audio is being written.");
            if (_completed)
                throw new InvalidOperationException("Video export encoder has already completed.");

            cancellationToken.ThrowIfCancellationRequested();
            _videoBeginning = true;
        }

        bool succeeded = false;
        try
        {
            await _encoder.BeginVideoAsync(cancellationToken).ConfigureAwait(false);
            // If cancellation arrived while the encoder started its video phase,
            // stop before exposing the mux as ready to accept frames.
            cancellationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        finally
        {
            lock (_stateGate)
            {
                if (succeeded)
                    _videoBegun = true;
                _videoBeginning = false;
            }
        }
    }

    internal async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (!_begun)
                throw new InvalidOperationException("Video export encoder has not begun.");
            if (_completed)
                return;
            if (_completing)
                throw new InvalidOperationException("Video export completion is already in progress.");
            // A direct mux caller must not finalize an encoder before video begins.
            // Keep this contract independent of the concrete ffmpeg implementation.
            if (!_videoBegun)
                throw new InvalidOperationException("Video export video phase has not begun.");
            if (_videoWriting)
                throw new InvalidOperationException("Video export cannot complete while a video frame is being written.");

            cancellationToken.ThrowIfCancellationRequested();
            _completing = true;
        }

        bool succeeded = false;
        try
        {
            await _encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
            // A custom encoder may finish completion even after cancellation.
            // Do not report the mux as successfully completed in that case.
            cancellationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        finally
        {
            lock (_stateGate)
            {
                if (succeeded)
                    _completed = true;
                _completing = false;
            }
        }
    }

    // Call only while holding _stateGate.
    private void EnsureAudioWritable()
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (_videoBegun || _videoBeginning)
            throw new InvalidOperationException("Video export audio phase has already ended or is ending.");
        if (_audioWriting)
            throw new InvalidOperationException("Video export audio write is already in progress.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");
    }

    // Call only while holding _stateGate.
    private void EnsureVideoWritable()
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (!_videoBegun)
            throw new InvalidOperationException("Video export video phase has not begun.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");
        if (_completing)
            throw new InvalidOperationException("Video export completion is in progress.");
        if (_videoWriting)
            throw new InvalidOperationException("Video export video write is already in progress.");
    }
}
