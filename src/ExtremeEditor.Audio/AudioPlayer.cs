using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private long _clockBaseDeviceBytes;
    private double _clockBaseAudioSeconds;
    private bool _clockReady;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _output is null || _output.PlaybackState == PlaybackState.Stopped;
    public string? LoadedPath { get; private set; }

    // WaveStream.CurrentTime reflects how far NAudio has read ahead while filling the
    // output buffers, not what the device is currently presenting. At an 80 ms
    // WaveOut latency that makes a render clock visibly advance in chunks. Drive the
    // editor from waveOutGetPosition instead so high-rate charts receive a smooth,
    // presentation-time clock.
    public TimeSpan Position
    {
        get
        {
            if (_reader is null)
                return TimeSpan.Zero;
            if (_output is null || !_clockReady)
                return _reader.CurrentTime;

            try
            {
                long deviceBytes = _output.GetPosition();
                long elapsedBytes = deviceBytes - _clockBaseDeviceBytes;
                if (elapsedBytes < 0)
                    elapsedBytes = 0;

                int bytesPerSecond = _reader.WaveFormat.AverageBytesPerSecond;
                if (bytesPerSecond <= 0)
                    return _reader.CurrentTime;

                double seconds = _clockBaseAudioSeconds + (double)elapsedBytes / bytesPerSecond;
                seconds = Math.Clamp(seconds, 0.0, _reader.TotalTime.TotalSeconds);
                return TimeSpan.FromSeconds(seconds);
            }
            catch
            {
                // Some waveOut drivers can fail position queries while the device is
                // being reset. Falling back here is preferable to breaking playback.
                return _reader.CurrentTime;
            }
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Load(string path)
    {
        DisposePlayback();

        string extension = Path.GetExtension(path);
        _reader = string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

        _output = new WaveOutEvent { DesiredLatency = 80 };
        _output.Init(_reader);
        ResetClock(0.0);
        LoadedPath = path;
    }

    public void Unload() => DisposePlayback();

    public void Play()
    {
        if (_output is null || _reader is null) return;

        if (Position >= _reader.TotalTime - TimeSpan.FromMilliseconds(1))
        {
            _reader.Position = 0;
            ResetClock(0.0);
        }

        _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
            _output.Pause();
    }

    public void Stop()
    {
        if (_output is null || _reader is null) return;
        _output.Stop();
        _reader.CurrentTime = TimeSpan.Zero;
        ResetClock(0.0);
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null || _output is null) return;

        double seconds = Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds);
        PlaybackState previousState = _output.PlaybackState;

        // waveOut may already contain decoded audio from the old position. Stop() is
        // used as a flush here so a seek cannot briefly replay stale buffered samples.
        _output.Stop();
        _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        ResetClock(seconds);

        if (previousState == PlaybackState.Playing)
            _output.Play();
        else if (previousState == PlaybackState.Paused)
        {
            // Keep the logical paused state after seeking. WaveOutEvent has no direct
            // way to enter Paused from Stopped without starting once.
            _output.Play();
            _output.Pause();
        }
    }

    public void Dispose()
    {
        DisposePlayback();
        GC.SuppressFinalize(this);
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
            // Position will temporarily fall back to WaveStream.CurrentTime.
        }
    }

    private void DisposePlayback()
    {
        _output?.Stop();
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
        _clockBaseDeviceBytes = 0;
        _clockBaseAudioSeconds = 0.0;
        _clockReady = false;
        LoadedPath = null;
    }
}
