using System.Diagnostics;
using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private const double PlaybackTimingSmoothing = 0.125;

    private double _playbackUiRollingMilliseconds;
    private double _playbackUiMaxMilliseconds;
    private double _playbackFrameRollingMilliseconds;
    private long _lastPlaybackUiTimestamp;
    private bool _playbackUiHasSample;

    public double PlaybackUiRollingMilliseconds => _playbackUiRollingMilliseconds;
    public double PlaybackUiMaxMilliseconds => _playbackUiMaxMilliseconds;
    public double PlaybackUiFps => _playbackFrameRollingMilliseconds > 0.000001
        ? 1000.0 / _playbackFrameRollingMilliseconds
        : 0.0;

    public string PlaybackDiagnosticsSnapshot =>
        $"playback-ui fps={PlaybackUiFps:F1} rolling={PlaybackUiRollingMilliseconds:F2}ms max={PlaybackUiMaxMilliseconds:F2}ms " +
        $"chunks req={Viewport.RasterChunkRequestsQueued} done={Viewport.RasterChunkBuildsCompleted} " +
        $"missing={Viewport.RasterVisibleMissingChunkCount} syncDense={Viewport.PlaybackSynchronousRasterBuildCount}";

    private long BeginPlaybackUiSample()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPlaybackUiTimestamp != 0)
        {
            double frameMilliseconds = (now - _lastPlaybackUiTimestamp) * 1000.0 / Stopwatch.Frequency;
            _playbackFrameRollingMilliseconds = _playbackFrameRollingMilliseconds <= 0.0
                ? frameMilliseconds
                : _playbackFrameRollingMilliseconds +
                  (frameMilliseconds - _playbackFrameRollingMilliseconds) * PlaybackTimingSmoothing;
        }

        _lastPlaybackUiTimestamp = now;
        return now;
    }

    private void EndPlaybackUiSample(long started)
    {
        double elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (!_playbackUiHasSample)
        {
            _playbackUiRollingMilliseconds = elapsedMilliseconds;
            _playbackUiHasSample = true;
        }
        else
        {
            _playbackUiRollingMilliseconds +=
                (elapsedMilliseconds - _playbackUiRollingMilliseconds) * PlaybackTimingSmoothing;
        }

        _playbackUiMaxMilliseconds = Math.Max(_playbackUiMaxMilliseconds, elapsedMilliseconds);
    }

    private void ResetPlaybackUiDiagnostics()
    {
        _playbackUiRollingMilliseconds = 0.0;
        _playbackUiMaxMilliseconds = 0.0;
        _playbackFrameRollingMilliseconds = 0.0;
        _lastPlaybackUiTimestamp = 0;
        _playbackUiHasSample = false;
    }

    private void HitSoundsToggleChanged(object sender, RoutedEventArgs e)
    {
        _audio.HitSoundsEnabled = HitSoundsToggle.IsChecked == true;
    }
}
