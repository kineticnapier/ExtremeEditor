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
        PlaybackSpeed = playbackSpeed;
    }

    public WaveFormat WaveFormat { get; }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (!PlaybackSpeedPolicy.TryNormalize(value, out double normalized))
                throw new ArgumentOutOfRangeException(nameof(value), "Playback speed must be finite and greater than zero.");
            _playbackSpeed = normalized;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int requestedFrames = count / _channels;
        if (requestedFrames <= 0 || !EnsureFrames())
            return 0;

        int writtenFrames = 0;
        while (writtenFrames < requestedFrames && _hasFirstFrame)
        {
            int destination = offset + writtenFrames * _channels;
            float mix = (float)_phase;
            for (int channel = 0; channel < _channels; channel++)
            {
                float first = _firstFrame[channel];
                float second = _hasSecondFrame ? _secondFrame[channel] : first;
                buffer[destination + channel] = first + (second - first) * mix;
            }

            writtenFrames++;
            _phase += _playbackSpeed;
            while (_phase >= 1.0 && _hasFirstFrame)
            {
                AdvanceSourceFrame();
                _phase -= 1.0;
            }
        }

        return writtenFrames * _channels;
    }

    public void Reset()
    {
        _sourceBufferOffset = 0;
        _sourceBufferCount = 0;
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
            _sourceBufferCount = _source.Read(_sourceBuffer, 0, _sourceBuffer.Length);
            _sourceBufferOffset = 0;
            if (_sourceBufferCount < _channels)
                return false;
        }

        Array.Copy(_sourceBuffer, _sourceBufferOffset, destination, 0, _channels);
        _sourceBufferOffset += _channels;
        return true;
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
