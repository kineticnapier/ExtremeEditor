using System.Windows;
using System.Windows.Input;

namespace ExtremeEditor.Wpf;

internal enum CleanPlayAction
{
    None,
    Enter,
    Exit
}

internal static class CleanPlayShortcut
{
    internal static CleanPlayAction Resolve(Key key, ModifierKeys modifiers, bool active)
    {
        if (!active && key == Key.Space && modifiers == ModifierKeys.Alt)
            return CleanPlayAction.Enter;
        if (active && key == Key.Escape && modifiers == ModifierKeys.None)
            return CleanPlayAction.Exit;
        return CleanPlayAction.None;
    }
}

public partial class MainWindow
{
    private CleanPlayLayout? _cleanPlayLayout;

    private bool IsCleanPlay => _cleanPlayLayout is not null;

    private bool TryHandleCleanPlayShortcut(
        KeyEventArgs e,
        Key key,
        ModifierKeys modifiers)
    {
        CleanPlayAction action = CleanPlayShortcut.Resolve(
            key,
            modifiers,
            IsCleanPlay);
        if (action == CleanPlayAction.None)
            return false;

        if (!e.IsRepeat)
            SetCleanPlay(action == CleanPlayAction.Enter);
        e.Handled = true;
        return true;
    }

    private void SetCleanPlay(bool enabled)
    {
        if (enabled == IsCleanPlay)
            return;

        if (enabled)
        {
            _cleanPlayLayout = new CleanPlayLayout(
                MainMenu.Visibility,
                ToolbarHost.Visibility,
                SettingsSidebarHost.Visibility,
                EventInspectorHost.Visibility,
                StatusBarHost.Visibility,
                DiagnosticsPanel.Visibility,
                SettingsRailColumn.Width,
                SettingsContentColumn.Width,
                EditorViewportColumn.Width,
                EventInspectorColumn.Width);

            MainMenu.Visibility = Visibility.Collapsed;
            ToolbarHost.Visibility = Visibility.Collapsed;
            SettingsSidebarHost.Visibility = Visibility.Collapsed;
            EventInspectorHost.Visibility = Visibility.Collapsed;
            StatusBarHost.Visibility = Visibility.Collapsed;
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
            SettingsRailColumn.Width = new GridLength(0);
            SettingsContentColumn.Width = new GridLength(0);
            EditorViewportColumn.Width = new GridLength(1, GridUnitType.Star);
            EventInspectorColumn.Width = new GridLength(0);
            NativeViewport.SetCleanView(true);
            StartCleanPlayPlayback();
        }
        else
        {
            PauseCleanPlayPlayback();
            NativeViewport.SetCleanView(false);
            CleanPlayLayout layout = _cleanPlayLayout!;
            MainMenu.Visibility = layout.MainMenuVisibility;
            ToolbarHost.Visibility = layout.ToolbarVisibility;
            SettingsSidebarHost.Visibility = layout.SettingsVisibility;
            EventInspectorHost.Visibility = layout.InspectorVisibility;
            StatusBarHost.Visibility = layout.StatusVisibility;
            DiagnosticsPanel.Visibility = layout.DiagnosticsVisibility;
            SettingsRailColumn.Width = layout.SettingsRailWidth;
            SettingsContentColumn.Width = layout.SettingsContentWidth;
            EditorViewportColumn.Width = layout.ViewportWidth;
            EventInspectorColumn.Width = layout.InspectorWidth;
            _cleanPlayLayout = null;
        }
    }

    private void StartCleanPlayPlayback()
    {
        FlushEditorPlaybackRefresh();
        if (_level is null || _timingMap is null || _audio.IsPlaying ||
            (_silentPlaybackActive && _silentPlaybackPlaying))
        {
            return;
        }

        TogglePlayback();
    }

    private void PauseCleanPlayPlayback()
    {
        if (_audio.IsLoaded)
        {
            if (_audio.IsPlaying)
                _audio.Pause();
        }
        else if (_silentPlaybackActive && _silentPlaybackPlaying)
        {
            _silentPlaybackChartTime = CurrentSilentChartTime;
            _silentPlaybackPlaying = false;
        }

        PlayButton.Content = "Play";
        CommandManager.InvalidateRequerySuggested();
        UpdatePlaybackDisplay();
    }

    private sealed record CleanPlayLayout(
        Visibility MainMenuVisibility,
        Visibility ToolbarVisibility,
        Visibility SettingsVisibility,
        Visibility InspectorVisibility,
        Visibility StatusVisibility,
        Visibility DiagnosticsVisibility,
        GridLength SettingsRailWidth,
        GridLength SettingsContentWidth,
        GridLength ViewportWidth,
        GridLength InspectorWidth);
}
