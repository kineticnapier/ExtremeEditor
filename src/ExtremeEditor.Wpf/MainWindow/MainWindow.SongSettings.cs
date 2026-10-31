using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Audio;
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
        RefreshTrackSettings();
        RefreshLevelMetadataSettings();
        RefreshMiscSettings();
    }

    private void SongSettingTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitSongSettingsFromUi();
        NativeViewport.FocusEditorSurface();
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

    private bool CommitSongSettingsFromUi(bool preserveInvalidInput = false)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _refreshingSongSettings)
            return true;

        LevelSettingsSnapshot before = editor.GetLevelSettings();
        if (!TryReadSetting(SongBpmTextBox.Text, out double bpm) || bpm <= 0 ||
            !TryReadSetting(SongVolumeTextBox.Text, out double volume) ||
            !TryReadSetting(SongOffsetTextBox.Text, out double offset) ||
            !TryReadSetting(SongPitchTextBox.Text, out double pitch) || pitch <= 0 ||
            !TryReadSetting(SongHitSoundVolumeTextBox.Text, out double hitSoundVolume))
        {
            if (!preserveInvalidInput)
                RefreshSongSettings();
            StatusText.Text = "Song Settings contain an invalid numeric value.";
            return false;
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
            return true;

        _ = ObserveSongSettingsChangeAsync(before, after);
        return true;
    }

    private bool TryCommitSettingsBeforeDocumentChange() => _settingsCategory switch
    {
        SettingsCategory.Song => CommitSongSettingsFromUi(preserveInvalidInput: true),
        SettingsCategory.Camera => CommitCameraSettingsFromUi(preserveInvalidInput: true),
        SettingsCategory.Track => CommitTrackSettingsFromUi(preserveInvalidInput: true),
        SettingsCategory.Level => CommitLevelMetadataSettingsFromUi(),
        _ => true
    };

    private async Task RefreshAfterSongSettingsChangeAsync(
        LevelSettingsSnapshot before,
        LevelSettingsSnapshot after)
    {
        if (_level is null || _documentTabs.ActiveTab is not EditorDocumentTab targetTab)
            return;

        LevelDocument targetLevel = _level;
        bool volumeOnly = IsSongVolumeOnlyChange(before, after);
        _audio.SongVolumePercent = after.SongVolumePercent;
        if (volumeOnly)
        {
            UpdateEditorStatus();
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        StopPlayback();
        bool songChanged = !string.Equals(before.SongFilename, after.SongFilename, StringComparison.Ordinal);
        bool nativeDependent = before.InitialBpm != after.InitialBpm || before.PitchPercent != after.PitchPercent;
        if (nativeDependent)
            NativeViewport.SetLevel(_level);

        WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(_level);
        _timingMap = playback.TimingMap;
        _hitSoundTimeline = playback.HitSoundTimeline;
        targetTab.TimingMap = playback.TimingMap;
        targetTab.HitSoundTimeline = playback.HitSoundTimeline;
        NativeViewport.SetPlaybackTimeline(_timingMap);

        RefreshInspector();
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();

        if (!songChanged && !targetTab.AudioPreparationPending)
        {
            _audio.ConfigureHitSounds(targetLevel, _timingMap, _hitSoundTimeline);
            _audio.SongVolumePercent = after.SongVolumePercent;
            return;
        }

        int generation = ++targetTab.AudioPreparationGeneration;
        targetTab.AudioPreparationPending = true;
        StatusText.Text = $"{targetTab.DisplayName} | audio preparing…";
        CommandManager.InvalidateRequerySuggested();

        PreparedAudioLoad prepared;
        try
        {
            prepared = await PreparedAudioLoad.PrepareAsync(
                targetLevel,
                playback,
                HitSoundsToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            if (!_isClosed &&
                _documentTabs.Tabs.Contains(targetTab) &&
                generation == targetTab.AudioPreparationGeneration)
            {
                targetTab.AudioPreparationPending = false;
                if (ReferenceEquals(_documentTabs.ActiveTab, targetTab))
                    StatusText.Text = $"audio preparation failed: {ex.Message}";
                CommandManager.InvalidateRequerySuggested();
            }
            return;
        }

        if (_isClosed || !CanApplyPreparedSongAudio(
                _documentTabs, targetTab, targetLevel, generation))
        {
            prepared.Dispose();
            return;
        }

        ApplySharedPlaybackPreferences(prepared.Player);
        prepared.Player.SongVolumePercent = targetLevel.SongVolumePercent;
        AudioPlayer? previousAudio = targetTab.Audio;
        targetTab.Audio = prepared.Player;
        targetTab.AudioPreparationPending = false;
        if (ReferenceEquals(_documentTabs.ActiveTab, targetTab))
        {
            _audio = prepared.Player;
            StatusText.Text = $"{targetTab.DisplayName} | {prepared.State}";
            UpdatePlaybackDisplay();
        }
        if (previousAudio is not null && !ReferenceEquals(previousAudio, prepared.Player))
            previousAudio.Dispose();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ObserveSongSettingsChangeAsync(
        LevelSettingsSnapshot before,
        LevelSettingsSnapshot after)
    {
        try
        {
            await RefreshAfterSongSettingsChangeAsync(before, after);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                StatusText.Text = $"Could not refresh song settings: {ex.Message}";
        }
    }

    internal static bool CanApplyPreparedSongAudio(
        EditorDocumentTabs tabs,
        EditorDocumentTab targetTab,
        LevelDocument targetLevel,
        int generation) =>
        tabs.Tabs.Contains(targetTab) &&
        ReferenceEquals(targetTab.Document, targetLevel) &&
        generation == targetTab.AudioPreparationGeneration;

    internal static bool IsSongVolumeOnlyChange(
        LevelSettingsSnapshot before,
        LevelSettingsSnapshot after) =>
        (before with { SongVolumePercent = after.SongVolumePercent }) == after;

    private static bool TryReadSetting(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        double.IsFinite(value);

    private static string FormatSetting(double value) =>
        value.ToString("R", CultureInfo.CurrentCulture);
}
