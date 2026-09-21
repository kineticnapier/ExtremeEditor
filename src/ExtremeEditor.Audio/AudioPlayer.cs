using System.Diagnostics;
using ExtremeEditor.Core;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExtremeEditor.Audio;

public readonly record struct AudioLoadMetrics(
    TimeSpan OpenReader,
    TimeSpan RenderHitSoundAssets,
    TimeSpan BuildHitSoundSchedule,
    TimeSpan RenderHitSoundChunks,
    TimeSpan WaveOutInit,
    TimeSpan Total,
    int SourceHitCount,
    int ScheduledHitCount,
    int RenderedChunkCount);

/// <summary>
/// Owns the decoder, unified song/hit-sound graph, output device and presentation
/// clock. There is exactly one WaveOutEvent for all audible playback. When a level
/// has no song, a finite silent bus takes the song's place so hit sounds still use
/// the exact same graph, limiter and device clock.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int HitSoundOnlySampleRate = 48_000;
    private const double HitSoundOnlyMinimumSeconds = 0.25;
    private const double HitSoundOnlyEndPaddingSeconds = 0.05;

    private readonly HitSoundLibrary _hitSoundLibrary = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private UnifiedAudioSampleProvider? _graph;
    private LevelDocument? _level;
    private TimingMap? _timingMap;
    private HitSoundTimeline? _hitSoundTimeline;
    private WaveFormat? _outputFormat;
    private long _clockBaseDeviceBytes;
    private double _clockBaseAudioSeconds;
    private double _transportDurationSeconds;
    private bool _clockReady;
    private bool _hitSoundsEnabled = true;

    public AudioPlayer()
    {
        using IDisposable? measurement = AudioDiagnosticLog.Shared?.Measure("audio_player.construct");
        _hitSoundLibrary.ReloadAssets();
    }

    /// <summary>True when either a song graph or a hit-sound-only graph is ready.</summary>
    public bool IsLoaded => _graph is not null && _output is not null;
    public bool HasSong => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _output is null || _output.PlaybackState == PlaybackState.Stopped;
    public string? LoadedPath { get; private set; }
    public AudioLoadMetrics LastLoadMetrics { get; private set; }
    public int LoadedHitSoundCount => _hitSoundLibrary.LoadedCount;
    public string HitSoundAssetSummary => _hitSoundLibrary.AssetSummary;
    public bool HitSoundsEnabled
    {
        get => _hitSoundsEnabled;
        set
        {
            if (_hitSoundsEnabled == value)
                return;

            _hitSoundsEnabled = value;
            if (_graph is not null)
                _graph.HitSoundsEnabled = value;
        }
    }

    // WaveStream.CurrentTime is a decoder read-ahead position. The device byte
    // position is the presentation clock shared by the song and hit sounds. This
    // also works for hit-sound-only playback because the clock belongs to WaveOut,
    // not to the decoder.
    public TimeSpan Position
    {
        get
        {
            if (_graph is null)
                return TimeSpan.Zero;

            double fallbackSeconds = _reader?.CurrentTime.TotalSeconds ?? _clockBaseAudioSeconds;
            if (_output is null || _outputFormat is null || !_clockReady)
                return TimeSpan.FromSeconds(Math.Clamp(fallbackSeconds, 0.0, _transportDurationSeconds));

            try
            {
                long elapsedBytes = _output.GetPosition() - _clockBaseDeviceBytes;
                if (elapsedBytes < 0)
                    elapsedBytes = 0;

                int bytesPerSecond = _outputFormat.AverageBytesPerSecond;
                if (bytesPerSecond <= 0)
                    return TimeSpan.FromSeconds(Math.Clamp(fallbackSeconds, 0.0, _transportDurationSeconds));

                double seconds = _clockBaseAudioSeconds + (double)elapsedBytes / bytesPerSecond;
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.0, _transportDurationSeconds));
            }
            catch
            {
                return TimeSpan.FromSeconds(Math.Clamp(fallbackSeconds, 0.0, _transportDurationSeconds));
            }
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0.0, _transportDurationSeconds));

    public void ConfigureHitSounds(LevelDocument? level, TimingMap? timingMap, HitSoundTimeline? timeline)
    {
        AudioDiagnosticLog.Shared?.Write("audio_player.configure_hitsounds",
            $"level_floors={level?.FloorCount ?? 0} timing_floors={timingMap?.Floors.Count ?? 0} " +
            $"timeline_changes={timeline?.StateChangeCount ?? 0} transport_loaded={IsLoaded} song_loaded={_reader is not null}");
        _level = level;
        _timingMap = timingMap;
        _hitSoundTimeline = timeline;
        if (_graph is not null)
            RebuildGraphPreservingTransport();
    }

    public void ReloadHitSoundAssets()
    {
        _hitSoundLibrary.ReloadAssets();
        if (_graph is not null)
            RebuildGraphPreservingTransport();
    }

    public void Load(string path)
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using IDisposable? measurement = log?.Measure("audio_player.load", $"path={path}");
        var totalWatch = Stopwatch.StartNew();
        LastLoadMetrics = default;

        using (log?.Measure("audio_player.dispose_before_load"))
            DisposePlayback();
        try
        {
            string extension = Path.GetExtension(path);
            var phaseWatch = Stopwatch.StartNew();
            using (log?.Measure("audio_player.open_reader", $"extension={extension}"))
            {
                _reader = string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(path)
                    : new AudioFileReader(path);
            }
            phaseWatch.Stop();
            TimeSpan openReader = phaseWatch.Elapsed;

            log?.Write("audio_player.reader_opened",
                $"provider={_reader.GetType().FullName} rate={_reader.WaveFormat.SampleRate} " +
                $"channels={_reader.WaveFormat.Channels} encoding={_reader.WaveFormat.Encoding} " +
                $"duration_ms={_reader.TotalTime.TotalMilliseconds:F3}");
            LoadedPath = path;

            BuildGraphMetrics graphMetrics;
            using (log?.Measure("audio_player.build_graph"))
                graphMetrics = BuildGraph();
            using (log?.Measure("audio_player.reset_clock"))
                ResetClock(0.0);

            totalWatch.Stop();
            LastLoadMetrics = CreateLoadMetrics(openReader, graphMetrics, totalWatch.Elapsed);
        }
        catch (Exception ex)
        {
            log?.Write("audio_player.load_failed",
                $"exception={ex.GetType().FullName} message={ex.Message}");
            DisposePlayback();
            throw;
        }
    }

    /// <summary>
    /// Builds the normal unified playback graph with silence in place of the song.
    /// This is intentionally not a metronome path: the exact hit-sound timeline is
    /// pre-rendered and mixed exactly as it is when a song is present.
    /// </summary>
    public void LoadHitSoundsOnly()
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using IDisposable? measurement = log?.Measure("audio_player.load_hitsounds_only");
        var totalWatch = Stopwatch.StartNew();
        LastLoadMetrics = default;

        DisposePlayback();
        try
        {
            BuildGraphMetrics graphMetrics = BuildGraph();
            ResetClock(0.0);
            totalWatch.Stop();
            LastLoadMetrics = CreateLoadMetrics(TimeSpan.Zero, graphMetrics, totalWatch.Elapsed);
        }
        catch (Exception ex)
        {
            log?.Write("audio_player.load_hitsounds_only_failed",
                $"exception={ex.GetType().FullName} message={ex.Message}");
            DisposePlayback();
            throw;
        }
    }

    public void Unload() => DisposePlayback();

    public void Play()
    {
        if (_output is null || _graph is null)
            return;

        if (Position >= Duration - TimeSpan.FromMilliseconds(1))
            Seek(TimeSpan.Zero);

        _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
            _output.Pause();
    }

    public void Stop()
    {
        if (_output is null || _graph is null)
            return;

        _output.Stop();
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.Zero;
        _graph.Seek(0);
        ResetClock(0.0);
    }

    public void Seek(TimeSpan position)
    {
        if (_output is null || _graph is null || _outputFormat is null)
            return;

        double seconds = Math.Clamp(position.TotalSeconds, 0, _transportDurationSeconds);
        PlaybackState previousState = _output.PlaybackState;
        _output.Stop();
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(seconds, _reader.TotalTime.TotalSeconds));
        long frame = SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(seconds, _outputFormat.SampleRate);
        _graph.Seek(frame);
        ResetClock(seconds);

        if (previousState == PlaybackState.Playing)
            _output.Play();
        else if (previousState == PlaybackState.Paused)
        {
            _output.Play();
            _output.Pause();
        }
    }

    public void Dispose()
    {
        DisposePlayback();
        GC.SuppressFinalize(this);
    }

    private BuildGraphMetrics BuildGraph()
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;

        ISampleProvider song;
        if (_reader is not null)
        {
            song = _reader.ToSampleProvider();
            if (song.WaveFormat.Channels == 1)
                song = new MonoToStereoSampleProvider(song);
            else if (song.WaveFormat.Channels > 2)
                song = new FirstTwoChannelsSampleProvider(song);
            _outputFormat = song.WaveFormat;
        }
        else
        {
            _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(HitSoundOnlySampleRate, 2);
            song = new SilentSampleProvider(_outputFormat);
        }

        log?.Write("audio_player.output_format",
            $"provider={song.GetType().FullName} rate={_outputFormat.SampleRate} " +
            $"channels={_outputFormat.Channels} encoding={_outputFormat.Encoding} " +
            $"hitsound_only={_reader is null}");

        TimeSpan renderAssets = TimeSpan.Zero;
        IReadOnlyDictionary<string, RenderedHitSound>? clips = null;
        if (_level is not null && _timingMap is not null && _hitSoundTimeline is not null &&
            _hitSoundLibrary.LoadedCount > 0)
        {
            var watch = Stopwatch.StartNew();
            clips = _hitSoundLibrary.RenderFor(_outputFormat.SampleRate);
            watch.Stop();
            renderAssets = watch.Elapsed;
        }

        long totalFrames = _reader is not null
            ? (long)Math.Ceiling(_reader.TotalTime.TotalSeconds * _outputFormat.SampleRate)
            : CalculateHitSoundOnlyFrames(_outputFormat.SampleRate, clips);
        _transportDurationSeconds = totalFrames <= 0
            ? 0.0
            : (double)totalFrames / _outputFormat.SampleRate;

        HitSoundRenderMetrics renderMetrics = default;
        SampleAccurateHitSoundProvider? hitSounds = null;
        if (clips is not null && _level is not null && _timingMap is not null && _hitSoundTimeline is not null)
        {
            using (log?.Measure("audio_player.render_hitsounds",
                       $"rate={_outputFormat.SampleRate} count={clips.Count}"))
            {
                hitSounds = new SampleAccurateHitSoundProvider(
                    _outputFormat,
                    _level,
                    _timingMap,
                    _hitSoundTimeline,
                    clips,
                    totalFrames);
                renderMetrics = hitSounds.Metrics;
            }
        }

        _graph = new UnifiedAudioSampleProvider(song, hitSounds, totalFrames)
        {
            HitSoundsEnabled = _hitSoundsEnabled
        };
        _output = new WaveOutEvent { DesiredLatency = 80 };
        log?.Write("audio_player.waveout_init_before",
            $"provider={_graph.GetType().FullName} rate={_graph.WaveFormat.SampleRate} " +
            $"channels={_graph.WaveFormat.Channels} total_frames={totalFrames}");

        var initWatch = Stopwatch.StartNew();
        using (log?.Measure("audio_player.waveout_init"))
            _output.Init(_graph.ToWaveProvider());
        initWatch.Stop();
        log?.Write("audio_player.waveout_init_after");

        return new BuildGraphMetrics(
            renderAssets,
            renderMetrics.BuildSchedule,
            renderMetrics.RenderChunks,
            initWatch.Elapsed,
            renderMetrics.SourceHitCount,
            renderMetrics.ScheduledHitCount,
            renderMetrics.RenderedChunkCount);
    }

    private long CalculateHitSoundOnlyFrames(
        int sampleRate,
        IReadOnlyDictionary<string, RenderedHitSound>? clips)
    {
        double chartEndAudio = 0.0;
        if (_level is not null && _timingMap is not null)
            chartEndAudio = PlaybackClock.ChartToAudioTime(_level, _timingMap.Duration);

        double tailSeconds = 0.0;
        if (clips is not null)
        {
            foreach (RenderedHitSound clip in clips.Values)
            {
                double clipSeconds = (double)clip.FrameCount / sampleRate;
                tailSeconds = Math.Max(tailSeconds, Math.Max(0.0, clipSeconds - clip.OffsetSeconds));
            }
        }

        double totalSeconds = Math.Max(
            HitSoundOnlyMinimumSeconds,
            Math.Max(0.0, chartEndAudio) + tailSeconds + HitSoundOnlyEndPaddingSeconds);
        return checked((long)Math.Ceiling(totalSeconds * sampleRate));
    }

    private void RebuildGraphPreservingTransport()
    {
        if (_graph is null)
            return;

        TimeSpan position = Position;
        PlaybackState state = _output?.PlaybackState ?? PlaybackState.Stopped;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _graph = null;
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, _reader.TotalTime.TotalSeconds));

        _ = BuildGraph();
        double restoredSeconds = Math.Clamp(position.TotalSeconds, 0.0, _transportDurationSeconds);
        _graph!.Seek(SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
            restoredSeconds, _outputFormat!.SampleRate));
        ResetClock(restoredSeconds);

        if (state == PlaybackState.Playing)
            _output!.Play();
        else if (state == PlaybackState.Paused)
        {
            _output!.Play();
            _output.Pause();
        }
    }

    private void ResetClock(double audioSeconds)
    {
        _clockBaseAudioSeconds = audioSeconds;
        _clockBaseDeviceBytes = 0;
        _clockReady = false;
        if (_output is null)
            return;

        try
        {
            _clockBaseDeviceBytes = _output.GetPosition();
            _clockReady = true;
        }
        catch
        {
            // Position temporarily falls back to the graph/decoder clock.
        }
    }

    private void DisposePlayback()
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using (log?.Measure("audio_player.output_stop", $"has_output={_output is not null}"))
            _output?.Stop();
        using (log?.Measure("audio_player.output_dispose", $"has_output={_output is not null}"))
            _output?.Dispose();
        using (log?.Measure("audio_player.reader_dispose", $"has_reader={_reader is not null}"))
            _reader?.Dispose();
        _output = null;
        _reader = null;
        _graph = null;
        _outputFormat = null;
        _transportDurationSeconds = 0.0;
        _clockBaseDeviceBytes = 0;
        _clockBaseAudioSeconds = 0.0;
        _clockReady = false;
        LoadedPath = null;
    }

    private static AudioLoadMetrics CreateLoadMetrics(
        TimeSpan openReader,
        BuildGraphMetrics graphMetrics,
        TimeSpan total) =>
        new(
            openReader,
            graphMetrics.RenderHitSoundAssets,
            graphMetrics.BuildHitSoundSchedule,
            graphMetrics.RenderHitSoundChunks,
            graphMetrics.WaveOutInit,
            total,
            graphMetrics.SourceHitCount,
            graphMetrics.ScheduledHitCount,
            graphMetrics.RenderedChunkCount);

    private readonly record struct BuildGraphMetrics(
        TimeSpan RenderHitSoundAssets,
        TimeSpan BuildHitSoundSchedule,
        TimeSpan RenderHitSoundChunks,
        TimeSpan WaveOutInit,
        int SourceHitCount,
        int ScheduledHitCount,
        int RenderedChunkCount);

    private sealed class SilentSampleProvider : ISampleProvider
    {
        public SilentSampleProvider(WaveFormat waveFormat)
        {
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    private sealed class FirstTwoChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public FirstTwoChannelsSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int requestedFrames = count / 2;
            int sourceSamples = requestedFrames * _source.WaveFormat.Channels;
            if (_sourceBuffer.Length < sourceSamples)
                _sourceBuffer = new float[sourceSamples];

            int read = _source.Read(_sourceBuffer, 0, sourceSamples);
            int framesRead = read / _source.WaveFormat.Channels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                int source = frame * _source.WaveFormat.Channels;
                int destination = offset + frame * 2;
                buffer[destination] = _sourceBuffer[source];
                buffer[destination + 1] = _sourceBuffer[source + 1];
            }

            return framesRead * 2;
        }
    }
}