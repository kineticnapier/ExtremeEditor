using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _refreshingLevelMetadataSettings;
    private LevelMetadataSettingsSnapshot? _displayedLevelMetadataSettings;
    private readonly HashSet<string> _editedLevelMetadataFields = new(StringComparer.Ordinal);

    private void LevelSettingsCategoryClick(object sender, RoutedEventArgs e)
    {
        _settingsCategory = SettingsCategory.Level;
        ApplySettingsPaneVisibility();
        RefreshLevelMetadataSettings();
    }

    private void RefreshLevelMetadataSettings()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        LevelMetadataSettingsSnapshot settings = editor.GetLevelMetadataSettings();
        _refreshingLevelMetadataSettings = true;
        try
        {
            LevelSongTitleTextBox.Text = settings.Song ?? string.Empty;
            LevelArtistTextBox.Text = settings.Artist ?? string.Empty;
            LevelAuthorTextBox.Text = settings.Author ?? string.Empty;
            LevelDescriptionTextBox.Text = settings.LevelDesc ?? string.Empty;
            LevelTagsTextBox.Text = settings.LevelTags ?? string.Empty;
            LevelArtistLinksTextBox.Text = settings.ArtistLinks ?? string.Empty;
            _displayedLevelMetadataSettings = settings;
            _editedLevelMetadataFields.Clear();
        }
        finally
        {
            _refreshingLevelMetadataSettings = false;
        }
    }

    private void LevelMetadataTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshingLevelMetadataSettings || sender is not FrameworkElement { Name.Length: > 0 } element)
            return;
        _editedLevelMetadataFields.Add(element.Name);
    }

    private void LevelMetadataSingleLinePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitLevelMetadataSettingsFromUi();
        NativeViewport.Focus();
        e.Handled = true;
    }

    private void LevelMetadataTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_refreshingLevelMetadataSettings)
            CommitLevelMetadataSettingsFromUi();
    }

    private void CommitLevelMetadataSettingsFromUi()
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _refreshingLevelMetadataSettings)
            return;

        LevelMetadataSettingsSnapshot before = editor.GetLevelMetadataSettings();
        LevelMetadataSettingsSnapshot baseline = _displayedLevelMetadataSettings ?? before;
        var requested = new LevelMetadataSettingsSnapshot(
            ReadLevelMetadataField(LevelSongTitleTextBox, baseline.Song),
            ReadLevelMetadataField(LevelArtistTextBox, baseline.Artist),
            ReadLevelMetadataField(LevelAuthorTextBox, baseline.Author),
            ReadLevelMetadataField(LevelDescriptionTextBox, baseline.LevelDesc),
            ReadLevelMetadataField(LevelTagsTextBox, baseline.LevelTags),
            ReadLevelMetadataField(LevelArtistLinksTextBox, baseline.ArtistLinks));
        editor.EditLevelMetadataSettings(requested);
        LevelMetadataSettingsSnapshot after = editor.GetLevelMetadataSettings();
        RefreshLevelMetadataSettings();
        if (before != after)
        {
            UpdateEditorStatus();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? ReadLevelMetadataField(TextBox textBox, string? baseline) =>
        !_editedLevelMetadataFields.Contains(textBox.Name) && baseline is null
            ? null
            : textBox.Text;
}
