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
    }

    internal async ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        EnsureAudioWritable();
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        await _encoder.WriteAudioChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
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
        _videoBegun = true;
    }

    internal async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_begun)
            throw new InvalidOperationException("Video export encoder has not begun.");
        if (_completed)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        await _encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
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
