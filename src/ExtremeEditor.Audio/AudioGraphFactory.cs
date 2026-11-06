using System.Diagnostics;
using ExtremeEditor.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExtremeEditor.Audio;

internal readonly record struct AudioGraphBuildResult(
    UnifiedAudioSampleProvider Graph,
    VariableRateSampleProvider SongRateProvider,
    VolumeSampleProvider SongVolumeProvider,
    TransportHitSoundSampleProvider? TransportHitSoundProvider,
    SampleAccurateHitSoundProvider? HitSoundProvider,
    HitSoundRenderMetrics HitSoundMetrics);

internal static class AudioGraphFactory
{
    internal const int DefaultHitSoundOnlySampleRate = 48_000;
    internal const double HitSoundOnlyMinimumSeconds = 0.25;
    internal const double HitSoundOnlyEndPaddingSeconds = 0.05;

    internal static ISampleProvider NormalizeSong(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        ISampleProvider song = source;
        if (song.WaveFormat.Channels == 1)
            song = new MonoToStereoSampleProvider(song);
        else if (song.WaveFormat.Channels > 2)
            song = new MultichannelFoldDownSampleProvider(song);

        return song;
    }

    internal static IReadOnlyDictionary<string, RenderedHitSound>? RenderHitSoundAssets(
        HitSoundLibrary library,
        int sampleRate,
        LevelDocument? level,
        TimingMap? timingMap,
        HitSoundTimeline? timeline,
        out TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (level is null || timingMap is null || timeline is null || library.LoadedCount <= 0)
        {
            elapsed = TimeSpan.Zero;
            return null;
        }

        var watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, RenderedHitSound> clips = library.RenderFor(sampleRate);
        watch.Stop();
        elapsed = watch.Elapsed;
        return clips;
    }

    internal static long CalculateHitSoundOnlyFrames(
        int sampleRate,
        LevelDocument? level,
        TimingMap? timingMap,
        IReadOnlyDictionary<string, RenderedHitSound>? clips)
    {
        double chartEndAudio = 0.0;
        if (level is not null && timingMap is not null)
        {
            // A malformed chart duration must not poison the fallback audio length.
            double durationAudio = PlaybackClock.ChartToAudioTime(level, timingMap.Duration);
            if (IsRepresentableAudioTime(durationAudio, sampleRate))
                chartEndAudio = durationAudio;
            foreach (LevelAction action in level.ActionStore.Actions)
            {
                if (!action.Active || action.Kind != LevelActionKind.PlaySound ||
                    clips is null || string.IsNullOrWhiteSpace(action.HitSound) ||
                    !TryResolveActionChartTime(timingMap, action, out double chartTime))
                {
                    continue;
                }

                // Only a PlaySound cue that can actually be scheduled should
                // extend the audio. Missing assets, "None" and muted events
                // must not create a long silent tail after the chart ends.
                string name = HitSoundLibrary.NormalizeName(action.HitSound);
                double volume = action.HitSoundVolumePercent ?? 100.0;
                if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) ||
                    !clips.TryGetValue(name, out RenderedHitSound? clip) ||
                    !double.IsFinite(volume) || volume <= 0.0)
                    continue;

                double eventAudio = PlaybackClock.ChartToAudioTime(level, chartTime);
                double startAudio = eventAudio - clip.OffsetSeconds;
                // The schedule converts the offset-adjusted start to a sample
                // position. Match that guard rather than accepting an event
                // whose source clip cannot be placed on the output clock.
                if (IsRepresentableAudioTime(eventAudio, sampleRate) &&
                    IsRepresentableAudioTime(startAudio, sampleRate))
                    chartEndAudio = Math.Max(chartEndAudio, eventAudio);
            }
        }

        double tailSeconds = 0.0;
        if (clips is not null)
        {
            foreach (RenderedHitSound clip in clips.Values)
            {
                double clipSeconds = (double)clip.FrameCount / sampleRate;
                double clipTailSeconds = clipSeconds - clip.OffsetSeconds;
                double clipTailFrames = clipTailSeconds * sampleRate;
                // Manifest offsets may be finite but too large for a sample
                // position. Such clips are omitted by the schedule as well.
                if (double.IsFinite(clipTailFrames) &&
                    clipTailFrames > 0.0 &&
                    clipTailFrames < 9223372036854775808d)
                    tailSeconds = Math.Max(tailSeconds, clipTailSeconds);
            }
        }

        double totalSeconds = Math.Max(
            HitSoundOnlyMinimumSeconds,
            Math.Max(0.0, chartEndAudio) + tailSeconds + HitSoundOnlyEndPaddingSeconds);
        // Each input time can fit in a long sample position while their sum
        // (including end padding) does not. Saturate the total length rather
        // than throwing when converting the rounded double frame count.
        double totalFrames = Math.Ceiling(totalSeconds * sampleRate);
        return totalFrames >= 9223372036854775808d
            ? long.MaxValue
            : (long)totalFrames;
    }

    private static bool IsRepresentableAudioTime(double seconds, int sampleRate)
    {
        double frames = seconds * sampleRate;
        // long.MaxValue rounds to 2^63 in double, which is exclusive.
        return double.IsFinite(frames) &&
               frames >= -9223372036854775808d &&
               frames < 9223372036854775808d;
    }

    internal static bool TryResolveActionChartTime(
        TimingMap timingMap,
        LevelAction action,
        out double chartTime)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(action);
        if ((uint)action.Floor >= (uint)timingMap.Floors.Count)
        {
            chartTime = 0;
            return false;
        }

        FloorTiming floor = timingMap.Floors[action.Floor];
        double bpm = floor.Bpm > 0 ? floor.Bpm : 100.0;
        chartTime = floor.EntryTime + (action.AngleOffset ?? 0.0) / 180.0 * (60.0 / bpm);
        return double.IsFinite(chartTime);
    }

    internal static long CalculatePlaybackFrames(
        long songFrames,
        int sampleRate,
        LevelDocument? level,
        TimingMap? timingMap,
        IReadOnlyDictionary<string, RenderedHitSound>? clips,
        double songPitch = 1.0)
    {
        double scaledSongFrames = Math.Ceiling(
            Math.Max(0, songFrames) / SongRatePolicy.Normalize(songPitch));
        // long.MaxValue rounds to 2^63 in double. A long song or a low
        // pitch can exceed that bound even when the decoded frame count fits.
        // Keep the song and hit-sound duration calculations equally bounded.
        long boundedSongFrames = scaledSongFrames >= 9223372036854775808d
            ? long.MaxValue
            : checked((long)scaledSongFrames);
        return Math.Max(
            boundedSongFrames,
            CalculateHitSoundOnlyFrames(sampleRate, level, timingMap, clips));
    }

    internal static AudioGraphBuildResult Build(
        ISampleProvider song,
        LevelDocument? level,
        TimingMap? timingMap,
        HitSoundTimeline? timeline,
        IReadOnlyDictionary<string, RenderedHitSound>? clips,
        long totalFrames,
        double songVolumePercent,
        double playbackSpeed,
        bool hitSoundsEnabled,
        double songPitch = 1.0)
    {
        ArgumentNullException.ThrowIfNull(song);
        // Math.Clamp preserves NaN; reject non-finite values before constructing
        // the playback graph so neither preview nor offline audio emits NaN PCM.
        if (!double.IsFinite(songVolumePercent))
            throw new ArgumentOutOfRangeException(nameof(songVolumePercent));

        SampleAccurateHitSoundProvider? hitSounds = null;
        HitSoundRenderMetrics renderMetrics = default;
        if (clips is not null && level is not null && timingMap is not null && timeline is not null)
        {
            hitSounds = new SampleAccurateHitSoundProvider(
                song.WaveFormat,
                level,
                timingMap,
                timeline,
                clips,
                totalFrames);
            renderMetrics = hitSounds.Metrics;
        }

        var songVolume = new VolumeSampleProvider(song)
        {
            Volume = (float)(Math.Clamp(songVolumePercent, 0.0, 100.0) * 0.01)
        };
        double effectiveSongRate = SongRatePolicy.Normalize(playbackSpeed * songPitch);
        var songRate = new VariableRateSampleProvider(songVolume, effectiveSongRate);
        TransportHitSoundSampleProvider? transportHitSounds = hitSounds is null
            ? null
            : new TransportHitSoundSampleProvider(hitSounds, playbackSpeed);
        var graph = new UnifiedAudioSampleProvider(songRate, transportHitSounds)
        {
            HitSoundsEnabled = hitSoundsEnabled
        };

        return new AudioGraphBuildResult(
            graph,
            songRate,
            songVolume,
            transportHitSounds,
            hitSounds,
            renderMetrics);
    }

    internal sealed class FiniteSilentSampleProvider : ISampleProvider
    {
        private long _positionFrames;

        internal FiniteSilentSampleProvider(WaveFormat waveFormat)
        {
            WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        }

        public WaveFormat WaveFormat { get; }
        internal long TotalFrames { get; set; } = long.MaxValue;

        public int Read(float[] buffer, int offset, int count)
        {
            int requestedFrames = count / WaveFormat.Channels;
            int frames = (int)Math.Min(
                requestedFrames,
                Math.Max(0, TotalFrames - _positionFrames));
            int samples = frames * WaveFormat.Channels;
            Array.Clear(buffer, offset, samples);
            _positionFrames += frames;
            return samples;
        }

        internal void Seek(long frame) =>
            _positionFrames = Math.Clamp(frame, 0, TotalFrames);
    }

    // Fallback for decoders that expose more than two channels without a
    // reliable speaker layout. Preserve extra-channel audio instead of dropping
    // it. This is not a speaker-mask-aware surround downmix.
    private sealed class MultichannelFoldDownSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly float _extraWeight;
        private float[] _sourceBuffer = [];
        // A decoder may return fewer than one complete interleaved frame.
        // Carry those samples into the next read rather than dropping them.
        private int _pendingSamples;
        private bool _hasPendingRightSample;
        private float _pendingRightSample;

        internal MultichannelFoldDownSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _extraWeight = 0.70710678f / (_channels - 2);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;

            int written = 0;
            // A caller may request an odd number of interleaved stereo samples.
            // Return the second sample of a previously converted frame first;
            // returning zero for a one-sample request falsely signals EOF.
            if (_hasPendingRightSample)
            {
                buffer[offset + written++] = _pendingRightSample;
                _hasPendingRightSample = false;
                if (written == count)
                    return written;
            }

            int remaining = count - written;
            int requestedFrames = remaining / 2 + (remaining & 1);
            // A legal output count can overflow when multiplied by the larger
            // source channel count. A short read is permitted by ISampleProvider.
            requestedFrames = Math.Min(requestedFrames, int.MaxValue / _channels);
            int sourceSamples = requestedFrames * _channels;
            if (_sourceBuffer.Length < sourceSamples)
                // Preserve a partial input frame when the next request is larger.
                Array.Resize(ref _sourceBuffer, sourceSamples);

            while (true)
            {
                // Read only missing samples, retaining a split input frame.
                int read = _source.Read(
                    _sourceBuffer, _pendingSamples, sourceSamples - _pendingSamples);
                if (read <= 0)
                {
                    _pendingSamples = 0; // Discard an incomplete final input frame.
                    return written;
                }

                int available = _pendingSamples + read;
                int framesRead = available / _channels;
                int trailingSamples = available - framesRead * _channels;
                for (int frame = 0; frame < framesRead; frame++)
                {
                    int sourceOffset = frame * _channels;
                    float extra = 0f;
                    for (int channel = 2; channel < _channels; channel++)
                        extra += _sourceBuffer[sourceOffset + channel];

                    float shared = extra * _extraWeight;
                    buffer[offset + written++] = _sourceBuffer[sourceOffset] + shared;
                    float right = _sourceBuffer[sourceOffset + 1] + shared;
                    if (written < count)
                        buffer[offset + written++] = right;
                    else
                    {
                        _pendingRightSample = right;
                        _hasPendingRightSample = true;
                    }
                }

                if (trailingSamples > 0)
                    Array.Copy(_sourceBuffer, framesRead * _channels,
                        _sourceBuffer, 0, trailingSamples);
                _pendingSamples = trailingSamples;
                if (framesRead > 0)
                    return written;
                // A short decoder read split a frame. Continue until complete.
            }
        }
    }
}
