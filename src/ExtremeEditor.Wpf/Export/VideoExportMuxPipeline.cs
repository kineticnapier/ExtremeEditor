using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf.Export;

internal sealed class VideoExportMuxPipeline
{
    private readonly IVideoEncoder _encoder;
    private bool _begun;
    private bool _videoBegun;
    private bool _completed;

    internal VideoExportMuxPipeline(IVideoEncoder encoder)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
    }

    internal async ValueTask BeginAsync(
        VideoEncoderStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        if (_begun)
            throw new InvalidOperationException("Video export encoder has already begun.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");

        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        await _encoder.BeginAsync(startInfo, cancellationToken).ConfigureAwait(false);
        // A custom encoder may finish BeginAsync despite cancellation. Do not
        // publish a usable mux state after cancellation; the caller disposes it.
        cancellationToken.ThrowIfCancellationRequested();
        _begun = true;
    }

    internal async ValueTask WriteVideoFrameAsync(
        byte[] rgb,
        CancellationToken cancellationToken)
    {
        EnsureVideoWritable();
        ArgumentNullException.ThrowIfNull(rgb);
        cancellationToken.ThrowIfCancellationRequested();

        await _encoder.WriteVideoFrameAsync(rgb, cancellationToken).ConfigureAwait(false);
        // An encoder may finish a write despite cancellation. Do not report
        // that the canceled frame was accepted successfully.
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        EnsureAudioWritable();
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        await _encoder.WriteAudioChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        // Observe cancellation raised during a non-cooperative audio write.
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async ValueTask BeginVideoAsync(CancellationToken cancellationToken)
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (_videoBegun)
            throw new InvalidOperationException("Video export video phase has already begun.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");

        cancellationToken.ThrowIfCancellationRequested();
        await _encoder.BeginVideoAsync(cancellationToken).ConfigureAwait(false);
        // If cancellation arrived while the encoder started its video phase,
        // stop before exposing the mux as ready to accept frames.
        cancellationToken.ThrowIfCancellationRequested();
        _videoBegun = true;
    }

    internal async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (_completed)
            return;
        // A direct mux caller must not finalize an encoder before video begins.
        // Keep this contract independent of the concrete ffmpeg implementation.
        if (!_videoBegun)
            throw new InvalidOperationException("Video export video phase has not begun.");

        cancellationToken.ThrowIfCancellationRequested();
        await _encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
        // A custom encoder may finish completion even after cancellation.
        // Do not report the mux as successfully completed in that case.
        cancellationToken.ThrowIfCancellationRequested();
        _completed = true;
    }

    private void EnsureAudioWritable()
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (_videoBegun)
            throw new InvalidOperationException("Video export audio phase has already ended.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");
    }

    private void EnsureVideoWritable()
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (!_videoBegun)
            throw new InvalidOperationException("Video export video phase has not begun.");
        if (_completed)
            throw new InvalidOperationException("Video export encoder has already completed.");
    }
}
