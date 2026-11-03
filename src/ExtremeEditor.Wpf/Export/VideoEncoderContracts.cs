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
        // libx264 with yuv420p cannot encode odd frame dimensions.
        if ((Width & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "H.264 yuv420p requires an even width.");
        if ((Height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(Height), "H.264 yuv420p requires an even height.");
        // RGB24 frames are passed as byte[]; reject dimensions that cannot fit
        // in a single managed array before creating files or starting ffmpeg.
        if ((long)Width * Height > Array.MaxLength / 3)
            throw new ArgumentOutOfRangeException(nameof(Width), "RGB24 frame exceeds the maximum supported array length.");
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
