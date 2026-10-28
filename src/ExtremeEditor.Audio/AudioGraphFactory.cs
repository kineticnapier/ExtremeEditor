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
            chartEndAudio = PlaybackClock.ChartToAudioTime(level, timingMap.Duration);

        double tailSeconds = 0.0;
        if (clips is not null)
        {
            foreach (RenderedHitSound clip in clips.Values)
            {
                double clipSeconds = (double)clip.FrameCount / sampleRate;
                tailSeconds = Math.Max(
                    tailSeconds,
                    Math.Max(0.0, clipSeconds - clip.OffsetSeconds));
            }
        }

        double totalSeconds = Math.Max(
            HitSoundOnlyMinimumSeconds,
            Math.Max(0.0, chartEndAudio) + tailSeconds + HitSoundOnlyEndPaddingSeconds);
        return checked((long)Math.Ceiling(totalSeconds * sampleRate));
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
        bool hitSoundsEnabled)
    {
        ArgumentNullException.ThrowIfNull(song);

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
        var songRate = new VariableRateSampleProvider(songVolume, playbackSpeed);
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
            int requestedFrames = count / 2;
            int sourceSamples = requestedFrames * _channels;
            if (_sourceBuffer.Length < sourceSamples)
                _sourceBuffer = new float[sourceSamples];

            int read = _source.Read(_sourceBuffer, 0, sourceSamples);
            int framesRead = read / _channels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                int sourceOffset = frame * _channels;
                int destination = offset + frame * 2;
                float extra = 0f;
                for (int channel = 2; channel < _channels; channel++)
                    extra += _sourceBuffer[sourceOffset + channel];

                float shared = extra * _extraWeight;
                buffer[destination] = _sourceBuffer[sourceOffset] + shared;
                buffer[destination + 1] = _sourceBuffer[sourceOffset + 1] + shared;
            }

            return framesRead * 2;
        }
    }
}
