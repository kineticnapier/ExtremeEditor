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

    private void MainWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (TryHandlePlaybackSpeedWheel(e.Delta, controlPressed))
            e.Handled = true;
    }

    private bool TryHandlePlaybackSpeedWheel(int delta, bool controlPressed)
    {
        if (!PlaybackSpeedWheel.TryStep(_playbackSpeed, delta, controlPressed, out double nextSpeed))
            return false;

        SetPlaybackSpeed(nextSpeed);
        return true;
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

internal static class PlaybackSpeedWheel
{
    private const decimal Step = 0.05m;
    private const decimal Minimum = 0.01m;
    private const decimal Maximum = 10.0m;

    public static bool TryStep(
        double currentSpeed,
        int wheelDelta,
        bool controlPressed,
        out double nextSpeed)
    {
        nextSpeed = currentSpeed;
        if (!controlPressed || wheelDelta == 0)
            return false;

        decimal current = (decimal)Math.Clamp(currentSpeed, (double)Minimum, (double)Maximum);
        decimal stepped = current + (wheelDelta > 0 ? Step : -Step);
        nextSpeed = (double)Math.Clamp(stepped, Minimum, Maximum);
        return true;
    }
}
