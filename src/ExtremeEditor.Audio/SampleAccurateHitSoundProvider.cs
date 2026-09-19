using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Renders precomputed hit sounds intersecting each requested sample range.
/// Floor/timing/timeline/name lookup work is completed in the constructor so
/// the realtime audio callback only advances a schedule and mixes PCM tails.
/// </summary>
internal sealed class SampleAccurateHitSoundProvider : ISampleProvider
{
    private readonly ScheduledHit[] _scheduledHits;
    private readonly List<ActiveVoice> _activeVoices = [];
    private readonly int _maxClipFrames;
    private long _positionFrames;
    private int _nextScheduledHit;

    public SampleAccurateHitSoundProvider(
        WaveFormat waveFormat,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat || waveFormat.Channels != 2)
            throw new ArgumentException("The hit-sound renderer requires stereo IEEE float audio.", nameof(waveFormat));

        WaveFormat = waveFormat;
        _scheduledHits = BuildSchedule(waveFormat.SampleRate, level, timingMap, timeline, clips);
        _maxClipFrames = _scheduledHits.Length == 0
            ? 0
            : _scheduledHits.Max(hit => hit.Clip.FrameCount);
        Seek(0);
    }

    public WaveFormat WaveFormat { get; }
    internal long PositionFrames => _positionFrames;

    public int Read(float[] buffer, int offset, int count)
    {
        int frameCount = count / WaveFormat.Channels;
        int sampleCount = frameCount * WaveFormat.Channels;
        Array.Clear(buffer, offset, sampleCount);
        if (frameCount == 0)
            return 0;

        long endFrame = _positionFrames + frameCount;
        QueueScheduledHitsBefore(endFrame);
        MixActiveVoices(buffer, offset, _positionFrames, endFrame);
        _positionFrames = endFrame;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        _positionFrames = Math.Max(0, positionFrames);
        _activeVoices.Clear();

        long earliestTailStart = _positionFrames - _maxClipFrames;
        _nextScheduledHit = LowerBoundStartFrame(earliestTailStart);
    }

    internal static long AudioTimeToSampleFrame(double audioSeconds, int sampleRate) =>
        checked((long)Math.Round(audioSeconds * sampleRate, MidpointRounding.AwayFromZero));

    private static ScheduledHit[] BuildSchedule(
        int sampleRate,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips)
    {
        if (clips.Count == 0)
            return [];

        int floorCount = Math.Min(level.FloorCount, timingMap.Floors.Count);
        if (floorCount <= 1)
            return [];

        var hits = new List<ScheduledHit>(floorCount - 1);
        for (int floor = 1; floor < floorCount; floor++)
        {
            if (timingMap.Floors[floor].MidSpin)
                continue;

            HitSoundState state = timeline.GetStateAtFloor(floor);
            if (string.Equals(state.Name, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            string name = HitSoundLibrary.NormalizeName(state.Name);
            if (!clips.TryGetValue(name, out RenderedHitSound? clip))
                continue;

            double floorAudio = PlaybackClock.ChartToAudioTime(level, timingMap.GetEntryTime(floor));
            double startAudio = floorAudio - clip.OffsetSeconds;
            long startFrame = AudioTimeToSampleFrame(startAudio, sampleRate);
            float volume = (float)Math.Clamp(state.Volume, 0.0, 1.0);
            hits.Add(new ScheduledHit(clip, startFrame, volume, floor));
        }

        hits.Sort(static (left, right) =>
        {
            int startOrder = left.StartFrame.CompareTo(right.StartFrame);
            return startOrder != 0 ? startOrder : left.Floor.CompareTo(right.Floor);
        });
        return hits.ToArray();
    }

    private int LowerBoundStartFrame(long startFrame)
    {
        int lo = 0;
        int hi = _scheduledHits.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_scheduledHits[mid].StartFrame < startFrame)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private void QueueScheduledHitsBefore(long endFrame)
    {
        while (_nextScheduledHit < _scheduledHits.Length)
        {
            ScheduledHit hit = _scheduledHits[_nextScheduledHit];
            if (hit.StartFrame >= endFrame)
                break;

            _nextScheduledHit++;
            if (hit.StartFrame + hit.Clip.FrameCount > _positionFrames)
                _activeVoices.Add(new ActiveVoice(hit.Clip, hit.StartFrame, hit.Volume));
        }
    }

    private void MixActiveVoices(float[] buffer, int offset, long startFrame, long endFrame)
    {
        for (int voiceIndex = _activeVoices.Count - 1; voiceIndex >= 0; voiceIndex--)
        {
            ActiveVoice voice = _activeVoices[voiceIndex];
            long voiceEnd = voice.StartFrame + voice.Clip.FrameCount;
            long mixStart = Math.Max(startFrame, voice.StartFrame);
            long mixEnd = Math.Min(endFrame, voiceEnd);

            for (long frame = mixStart; frame < mixEnd; frame++)
            {
                int source = checked((int)(frame - voice.StartFrame)) * 2;
                int destination = offset + checked((int)(frame - startFrame)) * 2;
                buffer[destination] += voice.Clip.Samples[source] * voice.Volume;
                buffer[destination + 1] += voice.Clip.Samples[source + 1] * voice.Volume;
            }

            if (voiceEnd <= endFrame)
                _activeVoices.RemoveAt(voiceIndex);
        }
    }

    private readonly record struct ScheduledHit(
        RenderedHitSound Clip,
        long StartFrame,
        float Volume,
        int Floor);

    private readonly record struct ActiveVoice(RenderedHitSound Clip, long StartFrame, float Volume);
}
