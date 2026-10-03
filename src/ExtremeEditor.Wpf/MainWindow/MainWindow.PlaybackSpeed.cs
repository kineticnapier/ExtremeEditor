using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private const double MinimumPlaybackSpeed = 0.01;
    private const double MaximumPlaybackSpeed = 10.0;
    private double _playbackSpeed = 1.0;

    private void PlaybackSpeedTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitPlaybackSpeedText();
    }

    private void PlaybackSpeedTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitPlaybackSpeedText();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ResetPlaybackSpeedClick(object sender, RoutedEventArgs e)
    {
        SetPlaybackSpeed(1.0);
    }

    private void CommitPlaybackSpeedText()
    {
        string text = PlaybackSpeedTextBox.Text.Trim();
        if (text.EndsWith('x') || text.EndsWith('X'))
            text = text[..^1].TrimEnd();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            UpdatePlaybackSpeedText();
            return;
        }

        SetPlaybackSpeed(value);
    }

    private void SetPlaybackSpeed(double requested)
    {
        if (!double.IsFinite(requested) || requested <= 0.0)
        {
            UpdatePlaybackSpeedText();
            return;
        }

        double normalized = Math.Clamp(requested, MinimumPlaybackSpeed, MaximumPlaybackSpeed);
        if (Math.Abs(normalized - _playbackSpeed) <= double.Epsilon)
        {
            UpdatePlaybackSpeedText();
            return;
        }

        if (_silentPlaybackActive && _silentPlaybackPlaying)
        {
            _silentPlaybackChartTime = CurrentSilentChartTime;
            _silentPlaybackTimestamp = Stopwatch.GetTimestamp();
        }

        _playbackSpeed = normalized;
        _audio.PlaybackSpeed = normalized;
        UpdatePlaybackSpeedText();

        if (_level is not null && _timingMap is not null)
        {
            if (_audio.IsLoaded)
            {
                double chartTime = PlaybackClock.AudioToChartTime(_level, _audio.Position.TotalSeconds);
                NativeViewport.SetPlaybackState(
                    chartTime,
                    EditorChartRate,
                    active: !_audio.IsStopped,
                    playing: _audio.IsPlaying);
            }
            else if (_silentPlaybackActive)
            {
                NativeViewport.SetPlaybackState(
                    _silentPlaybackChartTime,
                    EditorChartRate,
                    active: true,
                    playing: _silentPlaybackPlaying);
            }
        }

        UpdatePlaybackDisplay();
    }

    private void UpdatePlaybackSpeedText()
    {
        PlaybackSpeedTextBox.Text = $"{_playbackSpeed:0.##}x";
        PlaybackSpeedTextBox.ToolTip = $"Playback speed ({MinimumPlaybackSpeed:0.##}x–{MaximumPlaybackSpeed:0.##}x)";
    }
}
