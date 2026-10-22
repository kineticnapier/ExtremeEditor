using System.Diagnostics;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

internal readonly record struct AudioGraphRealtimeDiagnostics(
    long ReadCalls,
    long ReadTicks,
    long MaxReadTicks,
    long SongReadTicks,
    long HitSoundReadTicks,
    long LimiterTicks,
    long OverBudgetReadCalls,
    double MinimumMasterGain);

/// <summary>Combines song PCM and scheduled hit sounds on one absolute sample clock.</summary>
internal sealed class UnifiedAudioSampleProvider : ISampleProvider
{
    private const float MasterCeiling = 0.999f;
    private const float LimiterReleaseSeconds = 0.100f;

    private readonly ISampleProvider _song;
    private readonly SampleAccurateHitSoundProvider? _hitSounds;
    private readonly TransportHitSoundSampleProvider? _transportHitSounds;
    private readonly long _totalFrames;
    private readonly bool _transportMode;
    private float[] _hitBuffer = [];
    private long _positionFrames;
    private bool _hitSoundsEnabled = true;
    private float _limiterGain = 1.0f;
    private long _readCalls, _readTicks, _maxReadTicks, _songReadTicks,
        _hitSoundReadTicks, _limiterTicks, _overBudgetReadCalls;
    private double _minimumMasterGain = 1.0;

    internal AudioGraphRealtimeDiagnostics GetRealtimeDiagnostics() => new(
        Interlocked.Read(ref _readCalls),
        Interlocked.Read(ref _readTicks),
        Interlocked.Read(ref _maxReadTicks),
        Interlocked.Read(ref _songReadTicks),
        Interlocked.Read(ref _hitSoundReadTicks),
        Interlocked.Read(ref _limiterTicks),
        Interlocked.Read(ref _overBudgetReadCalls),
        Volatile.Read(ref _minimumMasterGain));

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

    public UnifiedAudioSampleProvider(
        ISampleProvider song,
        TransportHitSoundSampleProvider? hitSounds)
    {
        _song = song;
        _transportHitSounds = hitSounds;
        _totalFrames = long.MaxValue;
        _transportMode = true;
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
            if (value && !_transportMode)
                _hitSounds?.Seek(_positionFrames);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long started = Stopwatch.GetTimestamp();
        int read = _transportMode
            ? ReadTransport(buffer, offset, count)
            : ReadSourceClock(buffer, offset, count);
        RecordRead(started, read / WaveFormat.Channels);
        return read;
    }

    private int ReadSourceClock(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        long remainingFrames = _totalFrames - _positionFrames;
        if (remainingFrames <= 0)
            return 0;

        int requestedFrames = count / channels;
        int frameCount = (int)Math.Min(requestedFrames, remainingFrames);
        int sampleCount = frameCount * channels;
        long songStarted = Stopwatch.GetTimestamp();
        int songRead = _song.Read(buffer, offset, sampleCount);
        Interlocked.Add(ref _songReadTicks, Stopwatch.GetElapsedTime(songStarted).Ticks);
        if (songRead < sampleCount)
            Array.Clear(buffer, offset + songRead, sampleCount - songRead);

        if (_hitSounds is not null && _hitSoundsEnabled)
        {
            if (_hitBuffer.Length < sampleCount)
                _hitBuffer = new float[sampleCount];
            long hitStarted = Stopwatch.GetTimestamp();
            _hitSounds.Read(_hitBuffer, 0, sampleCount);
            Interlocked.Add(ref _hitSoundReadTicks, Stopwatch.GetElapsedTime(hitStarted).Ticks);
            for (int i = 0; i < sampleCount; i++)
                buffer[offset + i] += _hitBuffer[i];
        }

        long limiterStarted = Stopwatch.GetTimestamp();
        ApplyMasterLimiter(buffer, offset, frameCount, channels);
        Interlocked.Add(ref _limiterTicks, Stopwatch.GetElapsedTime(limiterStarted).Ticks);
        _positionFrames += frameCount;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        if (_transportMode)
        {
            _positionFrames = Math.Max(0, positionFrames);
            _transportHitSounds?.Seek(_positionFrames);
            _limiterGain = 1.0f;
            return;
        }

        _positionFrames = Math.Clamp(positionFrames, 0, _totalFrames);
        _hitSounds?.Seek(_positionFrames);
        _limiterGain = 1.0f;
    }

    public void ReanchorHitSounds(long sourceFrame, double playbackSpeed)
    {
        if (!_transportMode)
            return;
        _positionFrames = Math.Max(0, sourceFrame);
        _transportHitSounds?.ReanchorPlaybackSpeed(_positionFrames, playbackSpeed);
        _limiterGain = 1.0f;
    }

    private int ReadTransport(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int requestedSamples = count - count % channels;
        if (requestedSamples <= 0)
            return 0;

        Array.Clear(buffer, offset, requestedSamples);
        long songStarted = Stopwatch.GetTimestamp();
        int songRead = _song.Read(buffer, offset, requestedSamples);
        Interlocked.Add(ref _songReadTicks, Stopwatch.GetElapsedTime(songStarted).Ticks);
        int hitRead = 0;
        if (_transportHitSounds is not null)
        {
            if (_hitBuffer.Length < requestedSamples)
                _hitBuffer = new float[requestedSamples];
            long hitStarted = Stopwatch.GetTimestamp();
            hitRead = _transportHitSounds.Read(_hitBuffer, 0, requestedSamples);
            Interlocked.Add(ref _hitSoundReadTicks, Stopwatch.GetElapsedTime(hitStarted).Ticks);
            if (_hitSoundsEnabled)
            {
                for (int i = 0; i < hitRead; i++)
                    buffer[offset + i] += _hitBuffer[i];
            }
        }

        int sampleCount = Math.Max(songRead, hitRead);
        int frameCount = sampleCount / channels;
        if (frameCount <= 0)
            return 0;

        long limiterStarted = Stopwatch.GetTimestamp();
        ApplyMasterLimiter(buffer, offset, frameCount, channels);
        Interlocked.Add(ref _limiterTicks, Stopwatch.GetElapsedTime(limiterStarted).Ticks);
        return frameCount * channels;
    }

    private void RecordRead(long started, int frames)
    {
        long ticks = Stopwatch.GetElapsedTime(started).Ticks;
        Interlocked.Increment(ref _readCalls);
        Interlocked.Add(ref _readTicks, ticks);
        long peak = Interlocked.Read(ref _maxReadTicks);
        while (ticks > peak)
        {
            long observed = Interlocked.CompareExchange(ref _maxReadTicks, ticks, peak);
            if (observed == peak)
                break;
            peak = observed;
        }
        // Processing longer than the produced audio is a possible underrun
        // indicator; it does not itself prove that the device underruns.
        if (frames > 0 && ticks > (double)frames * TimeSpan.TicksPerSecond / WaveFormat.SampleRate)
            Interlocked.Increment(ref _overBudgetReadCalls);
    }

    private void RecordMinimumMasterGain(float gain)
    {
        double previous = Volatile.Read(ref _minimumMasterGain);
        while (gain < previous)
        {
            double observed = Interlocked.CompareExchange(ref _minimumMasterGain, gain, previous);
            if (observed == previous)
                break;
            previous = observed;
        }
    }

    private void ApplyMasterLimiter(float[] buffer, int offset, int frameCount, int channels)
    {
        float minimumGain = 1.0f;
        float releaseCoefficient = MathF.Exp(
            -1.0f / Math.Max(1.0f, WaveFormat.SampleRate * LimiterReleaseSeconds));

        int sample = offset;
        for (int frame = 0; frame < frameCount; frame++)
        {
            int frameOffset = sample;
            float framePeak = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                float value = buffer[sample + channel];
                if (!float.IsFinite(value))
                {
                    buffer[sample + channel] = 0.0f;
                    continue;
                }

                framePeak = Math.Max(framePeak, Math.Abs(value));
            }

            float targetGain = framePeak > MasterCeiling
                ? MasterCeiling / framePeak
                : 1.0f;

            // Linked-channel, zero-lookahead peak limiter: attack immediately on
            // the current frame, then recover smoothly. The waveform is scaled,
            // never flat-topped by per-sample Math.Clamp.
            if (targetGain < _limiterGain)
            {
                _limiterGain = targetGain;
            }
            else if (_limiterGain < targetGain)
            {
                float released = 1.0f - (1.0f - _limiterGain) * releaseCoefficient;
                _limiterGain = Math.Min(targetGain, released);
            }

            minimumGain = Math.Min(minimumGain, _limiterGain);
            for (int channel = 0; channel < channels; channel++)
                buffer[frameOffset + channel] *= _limiterGain;

            sample += channels;
        }
        RecordMinimumMasterGain(minimumGain);
    }
}
