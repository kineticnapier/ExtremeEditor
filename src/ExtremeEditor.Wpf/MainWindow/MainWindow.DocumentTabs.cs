using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    // The active chart's native renderer is still shared. Only document,
    // editing/undo, playback model, audio and selection are per tab.
    private readonly EditorDocumentTabs _documentTabs = new();

    private void ActiveEditorSessionChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            if (!_isClosed && ReferenceEquals(sender, _editor))
                RefreshActiveDocumentTab();
            return;
        }

        // SaveAsync may raise EditorSession.Changed off the UI thread.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_isClosed && ReferenceEquals(sender, _editor))
                RefreshActiveDocumentTab();
        }));
    }

    private void NewDocumentTabClick(object sender, RoutedEventArgs e)
    {
        CreateNewDocumentTab();
    }

    private void CreateNewDocumentTab()
    {
        if (!CanSwitchDocumentTabs())
            return;

        EnsureEditorSession();

        LevelDocument document = LevelDocument.CreateSynthetic(2);
        var editor = new EditorSession(document);
        var tab = new EditorDocumentTab(document, editor)
        {
            Audio = new AudioPlayer(),
            TimingMap = TimingMapBuilder.Build(document),
            SelectedFloors = [0],
            PrimaryFloor = 0
        };

        if (!_documentTabs.Add(tab, activate: false))
        {
            tab.Audio.Dispose();
            return;
        }

        ActivateDocumentTab(tab);
    }

    private bool CanSwitchDocumentTabs()
    {
        if (_isClosed || _isLoading || _playbackPreparationPending || IsCleanPlay)
            return false;

        if (_togetherModeActive)
        {
            MessageBox.Show(
                this,
                "Multi-tab editing is not available in a Together workspace yet.",
                "ExtremeEditor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void ActivateDocumentTab(EditorDocumentTab tab)
    {
        if (!CanSwitchDocumentTabs() ||
            !_documentTabs.Tabs.Contains(tab) ||
            ReferenceEquals(_documentTabs.ActiveTab, tab))
            return;

        // Resolve a pending edit's playback timing before leaving the current
        // chart, so no deferred timer can update a different tab afterwards.
        FlushEditorPlaybackRefresh();
        StopPlayback();

        EditorDocumentTab? previous = _documentTabs.ActiveTab;
        if (previous is not null)
        {
            previous.Audio = _audio;
            previous.TimingMap = _timingMap;
            previous.HitSoundTimeline = _hitSoundTimeline;
            previous.SelectedFloors = _selection.SelectedFloors.ToArray();
            previous.PrimaryFloor = _selection.PrimaryFloor;
        }

        _editorPlaybackRefreshTimer?.Stop();
        _editorPlaybackRefreshPending = false;
        _editorPlaybackRefreshDocument = null;

        _editor?.Changed -= ActiveEditorSessionChanged;
        _documentTabs.Activate(tab);

        _level = tab.Document;
        _editor = tab.Editor;
        _audio = tab.Audio ??= new AudioPlayer();
        _timingMap = tab.TimingMap;
        _hitSoundTimeline = tab.HitSoundTimeline;
        if (_timingMap is null)
        {
            WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(tab.Document);
            _timingMap = playback.TimingMap;
            _hitSoundTimeline = playback.HitSoundTimeline;
            tab.TimingMap = _timingMap;
            tab.HitSoundTimeline = _hitSoundTimeline;
        }

        // NativeLevelViewport remains a single HWND for this iteration.
        // Rebuild only the visible chart; inactive charts do not render.
        NativeViewport.SetLevel(tab.Document);
        NativeViewport.SetPlaybackTimeline(_timingMap);
        _selection.SetFloorCount(tab.Document.FloorCount);
        _selection.SetSelection(tab.SelectedFloors, tab.PrimaryFloor);
        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
        NativeViewport.ClearPlayback();

        _editor.Changed += ActiveEditorSessionChanged;
        RefreshInspector();
        RefreshMiscSettings();
        RefreshCameraSettings();
        RefreshTrackSettings();
        RefreshLevelMetadataSettings();
        RefreshActiveDocumentTab();
        UpdateEditorStatus();
        UpdatePlaybackDisplay();
        _discordPresence.SetEditing(tab.Document.FloorCount);
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshActiveDocumentTab()
    {
        EditorDocumentTab? active = _documentTabs.ActiveTab;
        if (active is null)
        {
            ActiveDocumentTabTitle.Text = "Untitled.adofai";
            ActiveDocumentTab.ToolTip = null;
        }
        else
        {
            ActiveDocumentTabTitle.Text = active.Header;
            string? path = active.Document.SourcePath;
            ActiveDocumentTab.ToolTip = path is null or "<synthetic>" ? null : path;
        }

        OtherDocumentTabsHost.Children.Clear();
        foreach (EditorDocumentTab tab in _documentTabs.Tabs)
        {
            if (ReferenceEquals(tab, active))
                continue;

            var button = new Button
            {
                Content = tab.Header,
                ToolTip = tab.Document.SourcePath is "<synthetic>" ? null : tab.Document.SourcePath,
                Tag = tab,
                MaxWidth = 290,
                MinWidth = 100,
                Margin = new Thickness(4, 0, 0, 3),
                Padding = new Thickness(12, 2, 12, 2),
                Background = new SolidColorBrush(Color.FromRgb(34, 38, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(192, 199, 209)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 69, 79))
            };
            button.Click += (_, _) => ActivateDocumentTab(tab);
            OtherDocumentTabsHost.Children.Add(button);
        }
    }
}
