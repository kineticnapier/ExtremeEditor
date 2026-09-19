using ExtremeEditor.Core;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExtremeEditor.Audio;

/// <summary>
/// Owns the decoder, unified song/hit-sound graph, output device and presentation
/// clock. There is exactly one WaveOutEvent for all audible playback.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
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
    private bool _clockReady;
    private bool _hitSoundsEnabled = true;

    public AudioPlayer()
    {
        using IDisposable? measurement = AudioDiagnosticLog.Shared?.Measure("audio_player.construct");
        _hitSoundLibrary.ReloadAssets();
    }

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _output is null || _output.PlaybackState == PlaybackState.Stopped;
    public string? LoadedPath { get; private set; }
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
    // position is the presentation clock shared by the song and hit sounds.
    public TimeSpan Position
    {
        get
        {
            if (_reader is null)
                return TimeSpan.Zero;
            if (_output is null || _outputFormat is null || !_clockReady)
                return _reader.CurrentTime;

            try
            {
                long elapsedBytes = _output.GetPosition() - _clockBaseDeviceBytes;
                if (elapsedBytes < 0)
                    elapsedBytes = 0;

                int bytesPerSecond = _outputFormat.AverageBytesPerSecond;
                if (bytesPerSecond <= 0)
                    return _reader.CurrentTime;

                double seconds = _clockBaseAudioSeconds + (double)elapsedBytes / bytesPerSecond;
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.0, _reader.TotalTime.TotalSeconds));
            }
            catch
            {
                return _reader.CurrentTime;
            }
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void ConfigureHitSounds(LevelDocument? level, TimingMap? timingMap, HitSoundTimeline? timeline)
    {
        AudioDiagnosticLog.Shared?.Write("audio_player.configure_hitsounds",
            $"level_floors={level?.FloorCount ?? 0} timing_floors={timingMap?.Floors.Count ?? 0} " +
            $"timeline_changes={timeline?.StateChangeCount ?? 0} reader_loaded={_reader is not null}");
        _level = level;
        _timingMap = timingMap;
        _hitSoundTimeline = timeline;
        if (_reader is not null)
            RebuildGraphPreservingTransport();
    }

    public void ReloadHitSoundAssets()
    {
        _hitSoundLibrary.ReloadAssets();
        if (_reader is not null)
            RebuildGraphPreservingTransport();
    }

    public void Load(string path)
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using IDisposable? measurement = log?.Measure("audio_player.load", $"path={path}");
        using (log?.Measure("audio_player.dispose_before_load"))
            DisposePlayback();
        try
        {
            string extension = Path.GetExtension(path);
            using (log?.Measure("audio_player.open_reader", $"extension={extension}"))
            {
                _reader = string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(path)
                    : new AudioFileReader(path);
            }
            log?.Write("audio_player.reader_opened",
                $"provider={_reader.GetType().FullName} rate={_reader.WaveFormat.SampleRate} " +
                $"channels={_reader.WaveFormat.Channels} encoding={_reader.WaveFormat.Encoding} " +
                $"duration_ms={_reader.TotalTime.TotalMilliseconds:F3}");
            LoadedPath = path;
            using (log?.Measure("audio_player.build_graph"))
                BuildGraph();
            using (log?.Measure("audio_player.reset_clock"))
                ResetClock(0.0);
        }
        catch (Exception ex)
        {
            log?.Write("audio_player.load_failed",
                $"exception={ex.GetType().FullName} message={ex.Message}");
            DisposePlayback();
            throw;
        }
    }

    public void Unload() => DisposePlayback();

    public void Play()
    {
        if (_output is null || _reader is null || _graph is null)
            return;

        if (Position >= _reader.TotalTime - TimeSpan.FromMilliseconds(1))
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
        if (_output is null || _reader is null || _graph is null)
            return;

        _output.Stop();
        _reader.CurrentTime = TimeSpan.Zero;
        _graph.Seek(0);
        ResetClock(0.0);
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null || _output is null || _graph is null || _outputFormat is null)
            return;

        double seconds = Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds);
        PlaybackState previousState = _output.PlaybackState;
        _output.Stop();
        _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
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

    private void BuildGraph()
    {
        if (_reader is null)
            return;

        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        ISampleProvider song = _reader.ToSampleProvider();
        if (song.WaveFormat.Channels == 1)
            song = new MonoToStereoSampleProvider(song);
        else if (song.WaveFormat.Channels > 2)
            song = new FirstTwoChannelsSampleProvider(song);

        _outputFormat = song.WaveFormat;
        log?.Write("audio_player.output_format",
            $"provider={song.GetType().FullName} rate={_outputFormat.SampleRate} " +
            $"channels={_outputFormat.Channels} encoding={_outputFormat.Encoding}");
        SampleAccurateHitSoundProvider? hitSounds = null;
        if (_level is not null && _timingMap is not null && _hitSoundTimeline is not null &&
            _hitSoundLibrary.LoadedCount > 0)
        {
            using (log?.Measure("audio_player.render_hitsounds",
                       $"rate={_outputFormat.SampleRate} count={_hitSoundLibrary.LoadedCount}"))
            {
                hitSounds = new SampleAccurateHitSoundProvider(
                    _outputFormat,
                    _level,
                    _timingMap,
                    _hitSoundTimeline,
                    _hitSoundLibrary.RenderFor(_outputFormat.SampleRate));
            }
        }

        long totalFrames = (long)Math.Ceiling(_reader.TotalTime.TotalSeconds * _outputFormat.SampleRate);
        _graph = new UnifiedAudioSampleProvider(song, hitSounds, totalFrames)
        {
            HitSoundsEnabled = _hitSoundsEnabled
        };
        _output = new WaveOutEvent { DesiredLatency = 80 };
        log?.Write("audio_player.waveout_init_before",
            $"provider={_graph.GetType().FullName} rate={_graph.WaveFormat.SampleRate} " +
            $"channels={_graph.WaveFormat.Channels} total_frames={totalFrames}");
        using (log?.Measure("audio_player.waveout_init"))
            _output.Init(_graph.ToWaveProvider());
        log?.Write("audio_player.waveout_init_after");
    }

    private void RebuildGraphPreservingTransport()
    {
        if (_reader is null)
            return;

        TimeSpan position = Position;
        PlaybackState state = _output?.PlaybackState ?? PlaybackState.Stopped;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _graph = null;
        _reader.CurrentTime = position;
        BuildGraph();
        _graph!.Seek(SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
            position.TotalSeconds, _outputFormat!.SampleRate));
        ResetClock(position.TotalSeconds);

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
            // Position temporarily falls back to the decoder read-ahead clock.
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
        _clockBaseDeviceBytes = 0;
        _clockBaseAudioSeconds = 0.0;
        _clockReady = false;
        LoadedPath = null;
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
