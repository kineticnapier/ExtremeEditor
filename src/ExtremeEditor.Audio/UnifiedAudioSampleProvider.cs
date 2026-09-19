using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>Combines song PCM and scheduled hit sounds on one absolute sample clock.</summary>
internal sealed class UnifiedAudioSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _song;
    private readonly SampleAccurateHitSoundProvider? _hitSounds;
    private readonly long _totalFrames;
    private float[] _hitBuffer = [];
    private long _positionFrames;
    private bool _hitSoundsEnabled = true;

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

        BoundPcm(buffer, offset, sampleCount);
        _positionFrames += frameCount;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        _positionFrames = Math.Clamp(positionFrames, 0, _totalFrames);
        _hitSounds?.Seek(_positionFrames);
    }

    private static void BoundPcm(float[] buffer, int offset, int count)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            float sample = buffer[i];
            buffer[i] = float.IsNaN(sample) ? 0.0f : Math.Clamp(sample, -1.0f, 1.0f);
        }
    }
}
