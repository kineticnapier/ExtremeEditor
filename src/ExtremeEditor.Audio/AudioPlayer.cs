using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _output is null || _output.PlaybackState == PlaybackState.Stopped;
    public string? LoadedPath { get; private set; }
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
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
        LoadedPath = path;
    }

    public void Play()
    {
        if (_output is null || _reader is null) return;
        if (_reader.Position >= _reader.Length)
            _reader.Position = 0;
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
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        double seconds = Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds);
        _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        DisposePlayback();
        GC.SuppressFinalize(this);
    }

    private void DisposePlayback()
    {
        _output?.Stop();
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
        LoadedPath = null;
    }
}
