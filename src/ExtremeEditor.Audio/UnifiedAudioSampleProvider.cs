using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>Combines song PCM and scheduled hit sounds on one absolute sample clock.</summary>
internal sealed class UnifiedAudioSampleProvider : ISampleProvider
{
    private const float MasterCeiling = 0.999f;
    private const float LimiterReleaseSeconds = 0.100f;

    private readonly ISampleProvider _song;
    private readonly SampleAccurateHitSoundProvider? _hitSounds;
    private readonly long _totalFrames;
    private float[] _hitBuffer = [];
    private long _positionFrames;
    private bool _hitSoundsEnabled = true;
    private float _limiterGain = 1.0f;

    public UnifiedAudioSampleProvider(
        ISampleProvider song,
        SampleAccurateHitSoundProvider? hitSounds,
        long totalFrames)
    {
        _song = song;
        _hitSounds = hitSounds;
        _totalFrames = Math.Max(0, totalFrames);
        WaveFormat = song.WaveFormat;

        if (hitSounds is not null && !WaveFormat.Equals(hitSounds.WaveFormat))
            throw new ArgumentException("Song and hit-sound formats must match.", nameof(hitSounds));
    }

    public WaveFormat WaveFormat { get; }

    public bool HitSoundsEnabled
    {
        get => _hitSoundsEnabled;
        set
        {
            if (_hitSoundsEnabled == value)
                return;

            _hitSoundsEnabled = value;
            if (value)
                _hitSounds?.Seek(_positionFrames);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        long remainingFrames = _totalFrames - _positionFrames;
        if (remainingFrames <= 0)
            return 0;

        int requestedFrames = count / channels;
        int frameCount = (int)Math.Min(requestedFrames, remainingFrames);
        int sampleCount = frameCount * channels;
        int songRead = _song.Read(buffer, offset, sampleCount);
        if (songRead < sampleCount)
            Array.Clear(buffer, offset + songRead, sampleCount - songRead);

        if (_hitSounds is not null && _hitSoundsEnabled)
        {
            if (_hitBuffer.Length < sampleCount)
                _hitBuffer = new float[sampleCount];
            _hitSounds.Read(_hitBuffer, 0, sampleCount);
            for (int i = 0; i < sampleCount; i++)
                buffer[offset + i] += _hitBuffer[i];
        }

        ApplyMasterLimiter(buffer, offset, frameCount, channels);
        _positionFrames += frameCount;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        _positionFrames = Math.Clamp(positionFrames, 0, _totalFrames);
        _hitSounds?.Seek(_positionFrames);
        _limiterGain = 1.0f;
    }

    private void ApplyMasterLimiter(float[] buffer, int offset, int frameCount, int channels)
    {
        int sampleCount = frameCount * channels;
        int end = offset + sampleCount;
        float peak = 0.0f;

        for (int i = offset; i < end; i++)
        {
            float sample = buffer[i];
            if (!float.IsFinite(sample))
            {
                buffer[i] = 0.0f;
                continue;
            }

            peak = Math.Max(peak, Math.Abs(sample));
        }

        float targetGain = peak > MasterCeiling
            ? MasterCeiling / peak
            : 1.0f;

        // Attack is immediate so a newly arriving transient cannot clip. Release
        // is smoothed per frame so dense hit-sound passages do not turn into the
        // flat-topped waveform produced by the old Math.Clamp path.
        if (targetGain < _limiterGain)
            _limiterGain = targetGain;

        float releaseCoefficient = MathF.Exp(
            -1.0f / Math.Max(1.0f, WaveFormat.SampleRate * LimiterReleaseSeconds));

        int sample = offset;
        for (int frame = 0; frame < frameCount; frame++)
        {
            if (_limiterGain < targetGain)
            {
                float released = 1.0f - (1.0f - _limiterGain) * releaseCoefficient;
                _limiterGain = Math.Min(targetGain, released);
            }

            for (int channel = 0; channel < channels; channel++)
                buffer[sample++] *= _limiterGain;
        }
    }
}
