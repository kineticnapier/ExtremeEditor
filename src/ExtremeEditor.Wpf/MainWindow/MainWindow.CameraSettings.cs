using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _refreshingCameraSettings;
    private SettingsCategory _settingsCategory = SettingsCategory.Song;

    private void RefreshCameraSettings()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        CameraSettingsSnapshot settings = editor.GetCameraSettings();
        _refreshingCameraSettings = true;
        try
        {
            CameraPositionXTextBox.Text = FormatSetting(settings.PositionX);
            CameraPositionYTextBox.Text = FormatSetting(settings.PositionY);
            CameraRotationTextBox.Text = FormatSetting(settings.Rotation);
            CameraZoomTextBox.Text = FormatSetting(settings.Zoom);

            ComboBoxItem? matching = CameraRelativeToComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Content?.ToString(), settings.RelativeTo, StringComparison.OrdinalIgnoreCase));
            if (matching is null)
            {
                matching = new ComboBoxItem { Content = settings.RelativeTo };
                CameraRelativeToComboBox.Items.Add(matching);
            }
            CameraRelativeToComboBox.SelectedItem = matching;
        }
        finally
        {
            _refreshingCameraSettings = false;
        }
    }

    private void SongSettingsCategoryClick(object sender, RoutedEventArgs e)
    {
        _settingsCategory = SettingsCategory.Song;
        ApplySettingsPaneVisibility();
        RefreshSongSettings();
    }

    private void CameraSettingsCategoryClick(object sender, RoutedEventArgs e)
    {
        _settingsCategory = SettingsCategory.Camera;
        ApplySettingsPaneVisibility();
        RefreshCameraSettings();
    }

    private void ApplySettingsPaneVisibility()
    {
        if (_settingsPaneCollapsed)
        {
            SongSettingsPane.Visibility = Visibility.Collapsed;
            CameraSettingsPane.Visibility = Visibility.Collapsed;
            TrackSettingsPane.Visibility = Visibility.Collapsed;
            LevelSettingsPane.Visibility = Visibility.Collapsed;
            return;
        }

        SongSettingsCategoryButton.IsChecked = _settingsCategory == SettingsCategory.Song;
        CameraSettingsCategoryButton.IsChecked = _settingsCategory == SettingsCategory.Camera;
        TrackSettingsCategoryButton.IsChecked = _settingsCategory == SettingsCategory.Track;
        LevelSettingsCategoryButton.IsChecked = _settingsCategory == SettingsCategory.Level;
        SongSettingsPane.Visibility = _settingsCategory == SettingsCategory.Song
            ? Visibility.Visible : Visibility.Collapsed;
        CameraSettingsPane.Visibility = _settingsCategory == SettingsCategory.Camera
            ? Visibility.Visible : Visibility.Collapsed;
        TrackSettingsPane.Visibility = _settingsCategory == SettingsCategory.Track
            ? Visibility.Visible : Visibility.Collapsed;
        LevelSettingsPane.Visibility = _settingsCategory == SettingsCategory.Level
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CameraSettingTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitCameraSettingsFromUi();
        NativeViewport.Focus();
        e.Handled = true;
    }

    private void CameraSettingTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_refreshingCameraSettings)
            CommitCameraSettingsFromUi();
    }

    private void CameraRelativeToSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingCameraSettings && IsLoaded)
            CommitCameraSettingsFromUi();
    }

    private void CommitCameraSettingsFromUi()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _refreshingCameraSettings)
            return;

        CameraSettingsSnapshot before = editor.GetCameraSettings();
        if (!TryReadSetting(CameraPositionXTextBox.Text, out double x) ||
            !TryReadSetting(CameraPositionYTextBox.Text, out double y) ||
            !TryReadSetting(CameraRotationTextBox.Text, out double rotation) ||
            !TryReadSetting(CameraZoomTextBox.Text, out double zoom) || zoom <= 0.0)
        {
            RefreshCameraSettings();
            StatusText.Text = "Camera Settings contain an invalid numeric value.";
            return;
        }

        string relativeTo = (CameraRelativeToComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                            ?? before.RelativeTo;
        var requested = new CameraSettingsSnapshot(relativeTo, x, y, rotation, zoom);
        editor.EditCameraSettings(requested);
        CameraSettingsSnapshot after = editor.GetCameraSettings();
        RefreshCameraSettings();
        if (before == after)
            return;

        RefreshAfterCameraSettingsCommit(before, after);
    }

    private void RefreshAfterCameraSettingsCommit(
        CameraSettingsSnapshot before,
        CameraSettingsSnapshot after)
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
