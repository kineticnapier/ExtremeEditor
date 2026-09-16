using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Renders only the hit sounds intersecting each requested sample range. The
/// floor cursor and currently audible tails are the only per-event state kept.
/// </summary>
internal sealed class SampleAccurateHitSoundProvider : ISampleProvider
{
    private readonly LevelDocument _level;
    private readonly TimingMap _timingMap;
    private readonly HitSoundTimeline _timeline;
    private readonly IReadOnlyDictionary<string, RenderedHitSound> _clips;
    private readonly List<ActiveVoice> _activeVoices = [];
    private readonly double _lookBackSeconds;
    private readonly double _maxPositiveOffsetSeconds;
    private long _positionFrames;
    private int _nextFloor;

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
        _level = level;
        _timingMap = timingMap;
        _timeline = timeline;
        _clips = clips;
        _lookBackSeconds = clips.Count == 0
            ? 0.0
            : clips.Values.Max(clip => clip.FrameCount / (double)waveFormat.SampleRate + Math.Abs(clip.OffsetSeconds));
        _maxPositiveOffsetSeconds = clips.Count == 0
            ? 0.0
            : Math.Max(0.0, clips.Values.Max(clip => clip.OffsetSeconds));
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
        QueueVoicesBefore(endFrame);
        MixActiveVoices(buffer, offset, _positionFrames, endFrame);
        _positionFrames = endFrame;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        _positionFrames = Math.Max(0, positionFrames);
        _activeVoices.Clear();

        double lookBackAudio = _positionFrames / (double)WaveFormat.SampleRate - _lookBackSeconds;
        double lookBackChart = PlaybackClock.AudioToChartTime(_level, lookBackAudio);
        _nextFloor = Math.Max(1, _timingMap.FindFirstFloorAtOrAfter(lookBackChart));
    }

    internal static long AudioTimeToSampleFrame(double audioSeconds, int sampleRate) =>
        checked((long)Math.Round(audioSeconds * sampleRate, MidpointRounding.AwayFromZero));

    private void QueueVoicesBefore(long endFrame)
    {
        while (_nextFloor < _level.FloorCount && _nextFloor < _timingMap.Floors.Count)
        {
            int floor = _nextFloor;
            double floorAudio = PlaybackClock.ChartToAudioTime(_level, _timingMap.GetEntryTime(floor));
            long earliestPossibleStartFrame = AudioTimeToSampleFrame(
                floorAudio - _maxPositiveOffsetSeconds, WaveFormat.SampleRate);
            if (earliestPossibleStartFrame >= endFrame)
                break;

            HitSoundState state = _timeline.GetStateAtFloor(floor);
            string name = HitSoundLibrary.NormalizeName(state.Name);
            _clips.TryGetValue(name, out RenderedHitSound? clip);

            double startAudio = floorAudio - (clip?.OffsetSeconds ?? 0.0);
            long startFrame = AudioTimeToSampleFrame(startAudio, WaveFormat.SampleRate);

            _nextFloor++;
            if (clip is null || _timingMap.Floors[floor].MidSpin ||
                string.Equals(state.Name, "None", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (startFrame + clip.FrameCount > _positionFrames)
                _activeVoices.Add(new ActiveVoice(clip, startFrame, (float)Math.Clamp(state.Volume, 0.0, 1.0)));
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

    private readonly record struct ActiveVoice(RenderedHitSound Clip, long StartFrame, float Volume);
}
