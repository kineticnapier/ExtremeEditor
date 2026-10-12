using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf.Export;

internal sealed record VideoEncoderStartInfo(
    string OutputPath,
    int Width,
    int Height,
    int FramesPerSecond,
    int AudioSampleRate,
    int AudioChannels)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Height));
        if (FramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (AudioSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(AudioSampleRate));
        if (AudioChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(AudioChannels));
    }
}

internal interface IVideoEncoder : IAsyncDisposable
{
    ValueTask BeginAsync(
        VideoEncoderStartInfo startInfo,
        CancellationToken cancellationToken);

    ValueTask WriteVideoFrameAsync(
        byte[] rgb,
        CancellationToken cancellationToken);

    ValueTask WriteAudioChunkAsync(
        OfflineAudioChunk chunk,
        CancellationToken cancellationToken);

    ValueTask BeginVideoAsync(CancellationToken cancellationToken);

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}
