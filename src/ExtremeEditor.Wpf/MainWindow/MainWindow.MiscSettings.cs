using System.Windows;
using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _refreshingMiscSettings;

    private void RefreshMiscSettings()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        MiscSettingsSnapshot settings = editor.GetMiscSettings();
        _refreshingMiscSettings = true;
        try
        {
            StickToFloorsCheckBox.IsChecked = settings.StickToFloors;
        }
        finally
        {
            _refreshingMiscSettings = false;
        }
    }

    private void MiscSettingsCategoryClick(object sender, RoutedEventArgs e)
    {
        _settingsCategory = SettingsCategory.Misc;
        ApplySettingsPaneVisibility();
        RefreshMiscSettings();
    }

    private void StickToFloorsCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingMiscSettings || !IsLoaded)
            return;

        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        MiscSettingsSnapshot before = editor.GetMiscSettings();
        editor.EditMiscSettings(new MiscSettingsSnapshot(StickToFloorsCheckBox.IsChecked == true));
        MiscSettingsSnapshot after = editor.GetMiscSettings();
        RefreshMiscSettings();
        if (before == after)
            return;

        RefreshAfterMiscSettingsCommit(before, after);
    }

    private void RefreshAfterMiscSettingsCommit(
        MiscSettingsSnapshot before,
        MiscSettingsSnapshot after)
    {
        if (_level is null || before == after)
            return;

        StopPlayback();
        NativeViewport.SetLevel(_level);
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }
}
