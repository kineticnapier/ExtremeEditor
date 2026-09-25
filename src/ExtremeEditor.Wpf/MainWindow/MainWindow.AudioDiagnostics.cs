using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private const double PlaybackTimingSmoothing = 0.125;
    private const double PlaybackGapLogThresholdMilliseconds = 50.0;
    private const double PlaybackDiagnosticsUiRefreshSeconds = 0.25;

    private readonly PlaybackDiagnosticLogger _playbackDiagnosticLogger;
    private double _playbackUiRollingMilliseconds;
    private double _playbackUiMaxMilliseconds;
    private double _playbackFrameRollingMilliseconds;
    private double _playbackTickMaxGapMilliseconds;
    private double _playbackRenderMaxGapMilliseconds;
    private double _lastPlaybackTickGapMilliseconds;
    private double _lastPlaybackRenderGapMilliseconds;
    private double _pendingTickStallMilliseconds;
    private double _pendingRenderStallMilliseconds;
    private long _lastPlaybackUiTimestamp;
    private long _lastPlaybackRenderTimestamp;
    private long _lastPlaybackDiagnosticsUiTimestamp;
    private bool _playbackUiHasSample;

    public double PlaybackUiRollingMilliseconds => _playbackUiRollingMilliseconds;
    public double PlaybackUiMaxMilliseconds => _playbackUiMaxMilliseconds;
    public double PlaybackTickMaxGapMilliseconds => _playbackTickMaxGapMilliseconds;
    public double PlaybackRenderMaxGapMilliseconds => _playbackRenderMaxGapMilliseconds;
    public double PlaybackUiFps => _playbackFrameRollingMilliseconds > 0.000001
        ? 1000.0 / _playbackFrameRollingMilliseconds
        : 0.0;

    public string PlaybackDiagnosticsSnapshot =>
        $"playback-ui fps={PlaybackUiFps:F1} rolling={PlaybackUiRollingMilliseconds:F2}ms max={PlaybackUiMaxMilliseconds:F2}ms " +
        $"tickMaxGap={PlaybackTickMaxGapMilliseconds:F1}ms renderMaxGap={PlaybackRenderMaxGapMilliseconds:F1}ms | " +
        NativeDiagnosticsSnapshot;

    private void UpdateDiagnosticsDisplay(string transportSummary)
    {
        PlaybackFpsText.Text = PlaybackUiFps > 0.0
            ? $"{PlaybackUiFps:F1} FPS"
            : "FPS --";
        PlaybackDiagnosticsText.Text = $"{transportSummary} | {PlaybackDiagnosticsSnapshot}";
    }

    private void DiagnosticsVisibilityChanged(object sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.Visibility = DiagnosticsMenuItem.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void HideDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsMenuItem.IsChecked = false;
    }

    private string NativeDiagnosticsSnapshot
    {
        get
        {
            if (!NativeViewport.TryGetDiagnostics(out var diagnostics))
                return "native=unavailable";

            return
                $"native fps={diagnostics.Fps:F1} frame={diagnostics.FrameMilliseconds:F2}ms " +
                $"max={diagnostics.MaxFrameMilliseconds:F2}ms render={diagnostics.RenderMilliseconds:F2}ms " +
                $"update={diagnostics.UpdateMilliseconds:F2}ms setup={diagnostics.DrawSetupMilliseconds:F2}ms " +
                $"cull={diagnostics.CullMilliseconds:F3}ms floorsMs={diagnostics.FloorMilliseconds:F2}ms " +
                $"iconsMs={diagnostics.IconMilliseconds:F2}ms overlay={diagnostics.OverlayMilliseconds:F2}ms " +
                $"endDraw={diagnostics.EndDrawMilliseconds:F2}ms present={diagnostics.PresentMilliseconds:F2}ms " +
                $"trackTotal={diagnostics.TrackTotalMilliseconds:F2}ms " +
                $"trackClock={diagnostics.TrackClockMilliseconds:F3}ms " +
                $"trackEval={diagnostics.TrackEvaluateMilliseconds:F2}ms " +
                $"trackApply={diagnostics.TrackApplyMilliseconds:F2}ms " +
                $"trackRm={diagnostics.TrackSpatialRemoveMilliseconds:F2}ms " +
                $"trackIns={diagnostics.TrackSpatialInsertMilliseconds:F2}ms " +
                $"trackCount={diagnostics.TrackTotalCount} active={diagnostics.TrackActiveCount} " +
                $"pos={diagnostics.TrackActivePositionCount} visualOnly={diagnostics.TrackActiveVisualOnlyCount} " +
                $"single={diagnostics.TrackActiveSingleEventCount} " +
                $"admit={diagnostics.TrackAdmittedCount} finish={diagnostics.TrackFinishedCount} " +
                $"eventScans={diagnostics.TrackEventScanCount} maxScan={diagnostics.TrackMaxEventScanCount} " +
                $"cand={diagnostics.VisibleCandidates} floors={diagnostics.FloorDraws} " +
                $"icons={diagnostics.IconDraws} calls={diagnostics.DrawCalls}";
        }
    }

    private long BeginPlaybackUiSample()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPlaybackUiTimestamp != 0)
        {
            double frameMilliseconds = (now - _lastPlaybackUiTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastPlaybackTickGapMilliseconds = frameMilliseconds;
            _playbackTickMaxGapMilliseconds = Math.Max(_playbackTickMaxGapMilliseconds, frameMilliseconds);
            if (frameMilliseconds >= PlaybackGapLogThresholdMilliseconds)
                _pendingTickStallMilliseconds = Math.Max(_pendingTickStallMilliseconds, frameMilliseconds);

            _playbackFrameRollingMilliseconds = _playbackFrameRollingMilliseconds <= 0.0
                ? frameMilliseconds
                : _playbackFrameRollingMilliseconds +
                  (frameMilliseconds - _playbackFrameRollingMilliseconds) * PlaybackTimingSmoothing;
        }

        _lastPlaybackUiTimestamp = now;
        return now;
    }

    private bool ShouldRefreshPlaybackDiagnostics(long nowTimestamp)
    {
        if (_lastPlaybackDiagnosticsUiTimestamp == 0 ||
            nowTimestamp < _lastPlaybackDiagnosticsUiTimestamp)
        {
            _lastPlaybackDiagnosticsUiTimestamp = nowTimestamp;
            return true;
        }

        long refreshTicks = Math.Max(
            1L,
            (long)(Stopwatch.Frequency * PlaybackDiagnosticsUiRefreshSeconds));
        if (nowTimestamp - _lastPlaybackDiagnosticsUiTimestamp < refreshTicks)
            return false;

        _lastPlaybackDiagnosticsUiTimestamp = nowTimestamp;
        return true;
    }

    private void PlaybackCompositionRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPlaybackRenderTimestamp != 0)
        {
            double gapMilliseconds = (now - _lastPlaybackRenderTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastPlaybackRenderGapMilliseconds = gapMilliseconds;
            _playbackRenderMaxGapMilliseconds = Math.Max(_playbackRenderMaxGapMilliseconds, gapMilliseconds);
            if (gapMilliseconds >= PlaybackGapLogThresholdMilliseconds)
                _pendingRenderStallMilliseconds = Math.Max(_pendingRenderStallMilliseconds, gapMilliseconds);
        }

        _lastPlaybackRenderTimestamp = now;
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

    private void LogPlaybackAnomalies(double chartTime)
    {
        bool hasTickStall = _pendingTickStallMilliseconds >= PlaybackGapLogThresholdMilliseconds;
        bool hasRenderStall = _pendingRenderStallMilliseconds >= PlaybackGapLogThresholdMilliseconds;
        if (!hasTickStall && !hasRenderStall)
            return;

        _playbackDiagnosticLogger.Log(
            $"playback-anomaly chartTime={chartTime:F6}s " +
            $"tickGap={_lastPlaybackTickGapMilliseconds:F1}ms renderGap={_lastPlaybackRenderGapMilliseconds:F1}ms " +
            $"tickStall={_pendingTickStallMilliseconds:F1}ms renderStall={_pendingRenderStallMilliseconds:F1}ms " +
            $"tickMaxGap={PlaybackTickMaxGapMilliseconds:F1}ms renderMaxGap={PlaybackRenderMaxGapMilliseconds:F1}ms " +
            NativeDiagnosticsSnapshot);

        _pendingTickStallMilliseconds = 0.0;
        _pendingRenderStallMilliseconds = 0.0;
    }

    private void ResetPlaybackUiDiagnostics()
    {
        _playbackUiRollingMilliseconds = 0.0;
        _playbackUiMaxMilliseconds = 0.0;
        _playbackFrameRollingMilliseconds = 0.0;
        _playbackTickMaxGapMilliseconds = 0.0;
        _playbackRenderMaxGapMilliseconds = 0.0;
        _lastPlaybackTickGapMilliseconds = 0.0;
        _lastPlaybackRenderGapMilliseconds = 0.0;
        _pendingTickStallMilliseconds = 0.0;
        _pendingRenderStallMilliseconds = 0.0;
        _lastPlaybackUiTimestamp = 0;
        _lastPlaybackRenderTimestamp = 0;
        _lastPlaybackDiagnosticsUiTimestamp = 0;
        _playbackUiHasSample = false;
    }

    private void HitSoundsToggleChanged(object sender, RoutedEventArgs e)
    {
        _audio.HitSoundsEnabled = HitSoundsToggle.IsChecked == true;
    }
}
