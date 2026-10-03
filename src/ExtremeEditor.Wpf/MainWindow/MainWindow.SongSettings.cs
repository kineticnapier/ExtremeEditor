using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _refreshingSongSettings;
    private bool _settingsPaneCollapsed;

    private void RefreshSongSettings()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        LevelSettingsSnapshot settings = editor.GetLevelSettings();
        _refreshingSongSettings = true;
        try
        {
            SongFilenameTextBox.Text = settings.SongFilename ?? string.Empty;
            SongBpmTextBox.Text = FormatSetting(settings.InitialBpm);
            SongVolumeTextBox.Text = FormatSetting(settings.SongVolumePercent);
            SongOffsetTextBox.Text = FormatSetting(settings.OffsetMilliseconds);
            SongPitchTextBox.Text = FormatSetting(settings.PitchPercent);
            SongHitSoundVolumeTextBox.Text = FormatSetting(settings.HitSoundVolumePercent);

            ComboBoxItem? matching = SongHitSoundComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Content?.ToString(), settings.DefaultHitSound, StringComparison.OrdinalIgnoreCase));
            if (matching is null)
            {
                matching = new ComboBoxItem { Content = settings.DefaultHitSound };
                SongHitSoundComboBox.Items.Add(matching);
            }
            SongHitSoundComboBox.SelectedItem = matching;
        }
        finally
        {
            _refreshingSongSettings = false;
        }

        RefreshCameraSettings();
    }

    private void SongSettingTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitSongSettingsFromUi();
        NativeViewport.Focus();
        e.Handled = true;
    }

    private void SongSettingTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_refreshingSongSettings)
            CommitSongSettingsFromUi();
    }

    private void SongHitSoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingSongSettings && IsLoaded)
            CommitSongSettingsFromUi();
    }

    private void SettingsSidebarCollapseClick(object sender, RoutedEventArgs e)
    {
        _settingsPaneCollapsed = !_settingsPaneCollapsed;
        SettingsContentColumn.Width = _settingsPaneCollapsed
            ? new GridLength(0)
            : new GridLength(280);
        ApplySettingsPaneVisibility();
        SettingsSidebarCollapseButton.Content = _settingsPaneCollapsed ? "›" : "‹";
        SettingsSidebarCollapseButton.ToolTip = _settingsPaneCollapsed ? "Expand settings" : "Collapse settings";
    }

    private void CommitSongSettingsFromUi()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _refreshingSongSettings)
            return;

        LevelSettingsSnapshot before = editor.GetLevelSettings();
        if (!TryReadSetting(SongBpmTextBox.Text, out double bpm) || bpm <= 0 ||
            !TryReadSetting(SongVolumeTextBox.Text, out double volume) ||
            !TryReadSetting(SongOffsetTextBox.Text, out double offset) ||
            !TryReadSetting(SongPitchTextBox.Text, out double pitch) || pitch <= 0 ||
            !TryReadSetting(SongHitSoundVolumeTextBox.Text, out double hitSoundVolume))
        {
            RefreshSongSettings();
            StatusText.Text = "Song Settings contain an invalid numeric value.";
            return;
        }

        string hitSound = (SongHitSoundComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                          ?? before.DefaultHitSound;
        var requested = new LevelSettingsSnapshot(
            string.IsNullOrWhiteSpace(SongFilenameTextBox.Text) ? null : SongFilenameTextBox.Text.Trim(),
            bpm,
            volume,
            offset,
            pitch,
            hitSound,
            hitSoundVolume);

        editor.EditLevelSettings(requested);
        LevelSettingsSnapshot after = editor.GetLevelSettings();
        RefreshSongSettings();
        if (before == after)
            return;

        RefreshAfterSongSettingsChange(before, after);
    }

    private void RefreshAfterSongSettingsChange(
        LevelSettingsSnapshot before,
        LevelSettingsSnapshot after)
    {
        if (_level is null)
            return;

        StopPlayback();
        _audio.SongVolumePercent = after.SongVolumePercent;

        bool volumeOnly = (before with { SongVolumePercent = after.SongVolumePercent }) == after;
        if (volumeOnly)
        {
            UpdateEditorStatus();
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        bool songChanged = !string.Equals(before.SongFilename, after.SongFilename, StringComparison.Ordinal);
        bool nativeDependent = before.InitialBpm != after.InitialBpm || before.PitchPercent != after.PitchPercent;
        if (nativeDependent)
            NativeViewport.SetLevel(_level);

        WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(_level);
        _timingMap = playback.TimingMap;
        _hitSoundTimeline = playback.HitSoundTimeline;
        NativeViewport.SetPlaybackTimeline(_timingMap);

        if (songChanged)
            _audio.Unload();
        _audio.ConfigureHitSounds(_level, _timingMap, _hitSoundTimeline);
        if (songChanged && playback.SongPath is string songPath && File.Exists(songPath))
            _ = LoadSong(songPath);
        _audio.SongVolumePercent = after.SongVolumePercent;

        RefreshInspector();
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool TryReadSetting(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        double.IsFinite(value);

    private static string FormatSetting(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);
}
