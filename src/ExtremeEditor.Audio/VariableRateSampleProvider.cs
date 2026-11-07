using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Consumes a source at a variable rate while preserving the device format.
/// AudioPlayer applies this to the song branch before fixed-rate hit sounds are mixed.
/// </summary>
internal sealed class VariableRateSampleProvider : ISampleProvider
{
    private const int SourceBufferFrames = 4096;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _sourceBuffer;
    private readonly float[] _firstFrame;
    private readonly float[] _secondFrame;
    private readonly float[] _pendingOutputFrame;
    private int _pendingOutputOffset;
    private int _sourceBufferOffset;
    private int _sourceBufferCount;
    private double _phase;
    private bool _hasFirstFrame;
    private bool _hasSecondFrame;
    private double _playbackSpeed;

    public VariableRateSampleProvider(ISampleProvider source, double playbackSpeed)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;
        _sourceBuffer = new float[SourceBufferFrames * _channels];
        _firstFrame = new float[_channels];
        _secondFrame = new float[_channels];
        _pendingOutputFrame = new float[_channels];
        _pendingOutputOffset = _channels;
        PlaybackSpeed = playbackSpeed;
    }

    public WaveFormat WaveFormat { get; }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            _playbackSpeed = SongRatePolicy.Normalize(value);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
            return 0;

        int written = 0;
        if (_pendingOutputOffset < _channels)
        {
            // An earlier odd-sized read may have returned only part of a frame.
            int pendingCount = Math.Min(count, _channels - _pendingOutputOffset);
            Array.Copy(_pendingOutputFrame, _pendingOutputOffset, buffer, offset, pendingCount);
            _pendingOutputOffset += pendingCount;
            written += pendingCount;
            if (_pendingOutputOffset < _channels || written == count)
                return written;
        }

        if (!EnsureFrames())
            return written;

        // Preserve the fast path for normal frame-aligned device reads.
        int requestedFrames = (count - written) / _channels;
        int writtenFrames = 0;
        while (writtenFrames < requestedFrames && _hasFirstFrame)
        {
            WriteInterpolatedFrame(buffer, offset + written + writtenFrames * _channels);
            writtenFrames++;
        }
        written += writtenFrames * _channels;

        if (written < count && _hasFirstFrame)
        {
            // Compute the final frame once and keep the undelivered channels
            // for the next call. Returning zero for count < Channels is not EOF.
            WriteInterpolatedFrame(_pendingOutputFrame, 0);
            int partialCount = count - written;
            Array.Copy(_pendingOutputFrame, 0, buffer, offset + written, partialCount);
            _pendingOutputOffset = partialCount;
            written += partialCount;
        }

        return written;
    }

    private void WriteInterpolatedFrame(float[] buffer, int offset)
    {
        float mix = (float)_phase;
        for (int channel = 0; channel < _channels; channel++)
        {
            float first = _firstFrame[channel];
            float second = _hasSecondFrame ? _secondFrame[channel] : first;
            buffer[offset + channel] = first + (second - first) * mix;
        }

        _phase += _playbackSpeed;
        while (_phase >= 1.0 && _hasFirstFrame)
        {
            AdvanceSourceFrame();
            _phase -= 1.0;
        }
    }

    // Offline rendering can start between source frames when pitch is not
    // an integer ratio. Preserve the fractional interpolation phase instead
    // of rounding the song seek to the preceding source frame.
    internal void SetInitialPhase(double fractionalSourceFrame)
    {
        if (!double.IsFinite(fractionalSourceFrame) ||
            fractionalSourceFrame < 0.0 || fractionalSourceFrame >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(fractionalSourceFrame));
        if (_hasFirstFrame || _hasSecondFrame || _sourceBufferCount != 0)
            throw new InvalidOperationException(
                "Initial phase can only be set before the first source read.");

        _phase = fractionalSourceFrame;
    }

    public void Reset()
    {
        _sourceBufferOffset = 0;
        _sourceBufferCount = 0;
        _pendingOutputOffset = _channels;
        _phase = 0.0;
        _hasFirstFrame = false;
        _hasSecondFrame = false;
    }

    private bool EnsureFrames()
    {
        if (!_hasFirstFrame)
            _hasFirstFrame = ReadSourceFrame(_firstFrame);
        if (_hasFirstFrame && !_hasSecondFrame)
            _hasSecondFrame = ReadSourceFrame(_secondFrame);
        return _hasFirstFrame;
    }

    private void AdvanceSourceFrame()
    {
        if (!_hasSecondFrame)
        {
            _hasFirstFrame = false;
            return;
        }

        Array.Copy(_secondFrame, _firstFrame, _channels);
        _hasFirstFrame = true;
        _hasSecondFrame = ReadSourceFrame(_secondFrame);
    }

    private bool ReadSourceFrame(float[] destination)
    {
        if (_sourceBufferCount - _sourceBufferOffset < _channels)
        {
            // A decoder may split an interleaved frame across short reads.
            // Keep its remaining samples when refilling the buffer, instead
            // of treating the short read as EOF or shifting the channels.
            int pending = _sourceBufferCount - _sourceBufferOffset;
            if (pending > 0)
                Array.Copy(_sourceBuffer, _sourceBufferOffset, _sourceBuffer, 0, pending);
            _sourceBufferOffset = 0;
            _sourceBufferCount = pending;
            while (_sourceBufferCount < _channels)
            {
                int read = _source.Read(
                    _sourceBuffer, _sourceBufferCount, _sourceBuffer.Length - _sourceBufferCount);
                if (read <= 0)
                    return false; // Ignore an incomplete final frame.
                _sourceBufferCount += read;
            }
        }

        Array.Copy(_sourceBuffer, _sourceBufferOffset, destination, 0, _channels);
        _sourceBufferOffset += _channels;
        return true;
    }
}

internal static class SongRatePolicy
{
    internal const double Minimum = 0.000001;
    internal const double Maximum = 100.0;

    internal static double Normalize(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(value), "Song rate must be finite and greater than zero.");
        return Math.Clamp(value, Minimum, Maximum);
    }

    internal static double FromPitchPercent(double pitchPercent)
    {
        double factor = pitchPercent * 0.01;
        return !double.IsFinite(factor) || factor <= 0.0
            ? 1.0
            : Math.Clamp(factor, Minimum, Maximum);
    }
}

internal static class PlaybackSpeedPolicy
{
    public const double Minimum = 0.01;
    public const double Maximum = 10.0;

    public static bool TryNormalize(double value, out double normalized)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            normalized = 0.0;
            return false;
        }

        normalized = Math.Clamp(value, Minimum, Maximum);
        return true;
    }
}
