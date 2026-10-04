using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal enum SettingsCategory
{
    Song,
    Camera,
    Track
}

public partial class MainWindow
{
    private bool _refreshingTrackSettings;

    private void RefreshTrackSettings()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        TrackSettingsSnapshot settings = editor.GetTrackSettings();
        _refreshingTrackSettings = true;
        try
        {
            TrackPrimaryColorTextBox.Text = settings.TrackColor;
            TrackSecondaryColorTextBox.Text = settings.SecondaryTrackColor;
            TrackColorAnimDurationTextBox.Text = FormatSetting(settings.TrackColorAnimDuration);
            TrackPulseLengthTextBox.Text = settings.TrackPulseLength.ToString();
            TrackGlowIntensityTextBox.Text = FormatSetting(settings.TrackGlowIntensity);
            SelectTrackSettingValue(TrackColorTypeComboBox, settings.TrackColorType);
            SelectTrackSettingValue(TrackColorPulseComboBox, settings.TrackColorPulse);
            SelectTrackSettingValue(TrackStyleComboBox, settings.TrackStyle);
        }
        finally
        {
            _refreshingTrackSettings = false;
        }
    }

    private static void SelectTrackSettingValue(ComboBox comboBox, string value)
    {
        ComboBoxItem? matching = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (matching is null)
        {
            matching = new ComboBoxItem { Content = value };
            comboBox.Items.Add(matching);
        }
        comboBox.SelectedItem = matching;
    }

    private void TrackSettingsCategoryClick(object sender, RoutedEventArgs e)
    {
        _settingsCategory = SettingsCategory.Track;
        ApplySettingsPaneVisibility();
        RefreshTrackSettings();
    }

    private void TrackSettingTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitTrackSettingsFromUi();
        NativeViewport.Focus();
        e.Handled = true;
    }

    private void TrackSettingTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_refreshingTrackSettings)
            CommitTrackSettingsFromUi();
    }

    private void TrackSettingComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingTrackSettings && IsLoaded)
            CommitTrackSettingsFromUi();
    }

    private void CommitTrackSettingsFromUi()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _refreshingTrackSettings)
            return;

        TrackSettingsSnapshot before = editor.GetTrackSettings();
        if (!EditorSession.IsValidTrackColor(TrackPrimaryColorTextBox.Text) ||
            !EditorSession.IsValidTrackColor(TrackSecondaryColorTextBox.Text) ||
            !TryReadSetting(TrackColorAnimDurationTextBox.Text, out double duration) || duration <= 0.0 ||
            !int.TryParse(TrackPulseLengthTextBox.Text, out int pulseLength) || pulseLength < 1 ||
            !TryReadSetting(TrackGlowIntensityTextBox.Text, out double glow) || glow is < 0.0 or > 100.0)
        {
            RefreshTrackSettings();
            StatusText.Text = "Track Settings contain an invalid value.";
            return;
        }

        string colorType = SelectedTrackSettingValue(TrackColorTypeComboBox, before.TrackColorType);
        string pulse = SelectedTrackSettingValue(TrackColorPulseComboBox, before.TrackColorPulse);
        string style = SelectedTrackSettingValue(TrackStyleComboBox, before.TrackStyle);
        var requested = new TrackSettingsSnapshot(
            colorType,
            TrackPrimaryColorTextBox.Text.Trim(),
            TrackSecondaryColorTextBox.Text.Trim(),
            duration,
            pulse,
            pulseLength,
            style,
            glow);

        editor.EditTrackSettings(requested);
        TrackSettingsSnapshot after = editor.GetTrackSettings();
        RefreshTrackSettings();
        if (before == after)
            return;
        RefreshAfterTrackSettingsCommit(before, after);
    }

    private static string SelectedTrackSettingValue(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private void RefreshAfterTrackSettingsCommit(
        TrackSettingsSnapshot before,
        TrackSettingsSnapshot after)
    {
        if (_level is null || before == after)
            return;

        StopPlayback();
        NativeViewport.SetLevel(_level);
        _timingMap ??= TimingMapBuilder.Build(_level);
        NativeViewport.SetPlaybackTimeline(_timingMap);
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }
}
