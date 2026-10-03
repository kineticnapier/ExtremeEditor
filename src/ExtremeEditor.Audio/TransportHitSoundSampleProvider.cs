using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Maps source-audio hit positions through piecewise playback-rate segments, then
/// plays each triggered voice at the fixed device rate. Playback speed changes
/// move future triggers without stretching voices that have already started.
/// </summary>
internal sealed class TransportHitSoundSampleProvider : ISampleProvider
{
    private readonly IReadOnlyList<SampleAccurateHitSoundProvider.ScheduledHit> _hits;
    private readonly long _totalSourceFrames;
    private readonly long _maxClipFrames;
    private readonly List<RateSegment> _segments = [];
    private readonly List<ActiveVoice> _activeVoices = [];
    private long _outputPositionFrames;
    private int _nextHitIndex;
    private double _playbackSpeed;

    public TransportHitSoundSampleProvider(
        SampleAccurateHitSoundProvider schedule,
        double playbackSpeed)
        : this(
            schedule?.WaveFormat ?? throw new ArgumentNullException(nameof(schedule)),
            schedule.ScheduledHits,
            schedule.TotalFrames,
            playbackSpeed)
    {
    }

    internal TransportHitSoundSampleProvider(
        WaveFormat waveFormat,
        IReadOnlyList<SampleAccurateHitSoundProvider.ScheduledHit> hits,
        long totalSourceFrames,
        double playbackSpeed)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        ArgumentNullException.ThrowIfNull(hits);
        WaveFormat = waveFormat;
        _hits = hits;
        _totalSourceFrames = Math.Max(0, totalSourceFrames);
        _maxClipFrames = _hits.Count == 0
            ? 0
            : _hits.Max(static hit => (long)hit.Clip.FrameCount);
        PlaybackSpeed = playbackSpeed;
        Seek(0);
    }

    public WaveFormat WaveFormat { get; }

    internal double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (!PlaybackSpeedPolicy.TryNormalize(value, out double normalized))
                throw new ArgumentOutOfRangeException(nameof(value));
            _playbackSpeed = normalized;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int requestedFrames = count / channels;
        if (requestedFrames <= 0)
            return 0;

        int writtenFrames = 0;
        while (writtenFrames < requestedFrames)
        {
            TriggerDueHits();
            RemoveFinishedVoices();

            double sourcePosition = OutputToSource(_outputPositionFrames);
            if (sourcePosition >= _totalSourceFrames &&
                _nextHitIndex >= _hits.Count &&
                _activeVoices.Count == 0)
            {
                break;
            }

            int destination = offset + writtenFrames * channels;
            for (int channel = 0; channel < channels; channel++)
                buffer[destination + channel] = 0.0f;

            foreach (ActiveVoice voice in _activeVoices)
            {
                long clipFrame = _outputPositionFrames - voice.StartOutputFrame;
                if ((ulong)clipFrame >= (ulong)voice.Hit.Clip.FrameCount)
                    continue;

                int source = checked((int)clipFrame) * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    buffer[destination + channel] +=
                        voice.Hit.Clip.Samples[source + channel] * voice.Hit.Volume;
                }
            }

            _outputPositionFrames++;
            writtenFrames++;
        }

        return writtenFrames * channels;
    }

    internal void Seek(long sourceFrame)
    {
        long clamped = Math.Clamp(sourceFrame, 0, _totalSourceFrames);
        _segments.Clear();
        _segments.Add(new RateSegment(clamped, 0, _playbackSpeed));
        _outputPositionFrames = 0;
        _nextHitIndex = LowerBound(clamped);
        RebuildActiveVoices(clamped, includeAnchor: false);
    }

    internal void ReanchorPlaybackSpeed(long sourceFrame, double playbackSpeed)
    {
        if (!PlaybackSpeedPolicy.TryNormalize(playbackSpeed, out double normalized))
            return;

        long clamped = Math.Clamp(sourceFrame, 0, _totalSourceFrames);
        long outputAnchor = SourceToOutput(clamped);

        int firstFutureSegment = _segments.FindIndex(segment => segment.SourceAnchor >= clamped);
        if (firstFutureSegment >= 0)
            _segments.RemoveRange(firstFutureSegment, _segments.Count - firstFutureSegment);

        _segments.Add(new RateSegment(clamped, outputAnchor, normalized));
        _playbackSpeed = normalized;
        _outputPositionFrames = outputAnchor;

        // An event at the anchor belongs to the completed side of the boundary.
        // Reconstruct it at age zero and advance beyond it so it cannot trigger twice.
        _nextHitIndex = UpperBound(clamped);
        RebuildActiveVoices(clamped, includeAnchor: true);
    }

    private void TriggerDueHits()
    {
        while (_nextHitIndex < _hits.Count)
        {
            SampleAccurateHitSoundProvider.ScheduledHit hit = _hits[_nextHitIndex];
            long triggerOutput = SourceToOutput(hit.StartFrame);
            if (triggerOutput > _outputPositionFrames)
                break;

            _activeVoices.Add(new ActiveVoice(hit, triggerOutput));
            _nextHitIndex++;
        }
    }

    private void RebuildActiveVoices(long sourceAnchor, bool includeAnchor)
    {
        _activeVoices.Clear();
        int index = (includeAnchor ? UpperBound(sourceAnchor) : LowerBound(sourceAnchor)) - 1;
        for (; index >= 0; index--)
        {
            SampleAccurateHitSoundProvider.ScheduledHit hit = _hits[index];
            long triggerOutput = SourceToOutput(hit.StartFrame);
            long age = _outputPositionFrames - triggerOutput;
            if (age >= _maxClipFrames)
                break;
            if (age >= 0 && age < hit.Clip.FrameCount)
                _activeVoices.Add(new ActiveVoice(hit, triggerOutput));
        }
    }

    private void RemoveFinishedVoices()
    {
        _activeVoices.RemoveAll(voice =>
            _outputPositionFrames - voice.StartOutputFrame >= voice.Hit.Clip.FrameCount);
    }

    private long SourceToOutput(long sourceFrame)
    {
        RateSegment segment = FindSegment(sourceFrame);
        return checked(segment.OutputAnchor + (long)Math.Round(
            (sourceFrame - segment.SourceAnchor) / segment.Speed,
            MidpointRounding.AwayFromZero));
    }

    private double OutputToSource(long outputFrame)
    {
        int low = 0;
        int high = _segments.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_segments[middle].OutputAnchor <= outputFrame)
                low = middle + 1;
            else
                high = middle;
        }
        RateSegment segment = _segments[Math.Max(0, low - 1)];
        return segment.SourceAnchor + (outputFrame - segment.OutputAnchor) * segment.Speed;
    }

    private RateSegment FindSegment(long sourceFrame)
    {
        int low = 0;
        int high = _segments.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_segments[middle].SourceAnchor <= sourceFrame)
                low = middle + 1;
            else
                high = middle;
        }
        return _segments[Math.Max(0, low - 1)];
    }

    private int LowerBound(long sourceFrame)
    {
        int low = 0;
        int high = _hits.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_hits[middle].StartFrame < sourceFrame)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private int UpperBound(long sourceFrame)
    {
        int low = 0;
        int high = _hits.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_hits[middle].StartFrame <= sourceFrame)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private readonly record struct RateSegment(long SourceAnchor, long OutputAnchor, double Speed);
    private readonly record struct ActiveVoice(
        SampleAccurateHitSoundProvider.ScheduledHit Hit,
        long StartOutputFrame);
}
