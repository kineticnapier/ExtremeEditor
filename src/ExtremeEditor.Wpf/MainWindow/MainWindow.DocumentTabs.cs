using System.Diagnostics;
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
        if (_isClosed || _isLoading || _playbackPreparationPending ||
            _tabCloseSavePending || IsCleanPlay)
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

    private void ActivateDocumentTab(
        EditorDocumentTab tab,
        Native.PreparedNativeLevel? preparedLevel = null,
        bool deferPlaybackTimeline = false)
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

        // NativeLevelViewport remains a single HWND. Restore the immutable
        // prepared snapshot for this document instead of rebuilding the entire
        // chart whenever the user returns to its tab.
        var preparationWatch = Stopwatch.StartNew();
        Native.PreparedNativeLevel nativeLevel = preparedLevel ??
            NativeLoadPreparationCache.StartLevel(tab.Document).GetAwaiter().GetResult();
        double levelWaitMs = preparationWatch.Elapsed.TotalMilliseconds;

        var uploadWatch = Stopwatch.StartNew();
        NativeLevelLoadMetrics levelMetrics =
            NativeViewport.SetPreparedLevelProfiled(tab.Document, nativeLevel);
        double levelUploadMs = uploadWatch.Elapsed.TotalMilliseconds;

        double playbackWaitMs = 0.0;
        double playbackUploadMs = 0.0;
        if (!deferPlaybackTimeline)
        {
            if (_timingMap is null)
                throw new InvalidOperationException("Active tab has no playback timing map.");

            preparationWatch.Restart();
            Native.PreparedNativePlayback playback =
                NativeLoadPreparationCache.StartPlayback(tab.Document, _timingMap)
                    .GetAwaiter().GetResult();
            playbackWaitMs = preparationWatch.Elapsed.TotalMilliseconds;

            uploadWatch.Restart();
            NativeViewport.SetPreparedPlaybackTimelineProfiled(playback);
            playbackUploadMs = uploadWatch.Elapsed.TotalMilliseconds;
        }

        Console.WriteLine(
            $"[tab-switch] chart={tab.DisplayName} " +
            $"levelWait={levelWaitMs:F1}ms levelUpload={levelUploadMs:F1}ms " +
            $"playbackWait={playbackWaitMs:F1}ms playbackUpload={playbackUploadMs:F1}ms " +
            $"snapshotBuild={levelMetrics.SnapshotBuild.TotalMilliseconds:F1}ms");
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

    private bool _tabCloseSavePending;

    private void CloseDocumentTab(EditorDocumentTab tab)
    {
        if (_tabCloseSavePending || !CanSwitchDocumentTabs() ||
            !_documentTabs.Tabs.Contains(tab))
            return;

        if (tab.IsDirty)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"Save changes to {tab.DisplayName} before closing?",
                "Unsaved chart",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel)
                return;
            if (answer == MessageBoxResult.Yes)
            {
                _ = SaveAndCloseDocumentTabAsync(tab);
                return;
            }
        }

        // Select the surviving tab first; removing an active tab from the
        // model without a workspace switch would desynchronize the viewport.
        if (ReferenceEquals(_documentTabs.ActiveTab, tab))
        {
            int index = _documentTabs.Tabs.ToList().IndexOf(tab);
            if (_documentTabs.Tabs.Count == 1)
            {
                CreateNewDocumentTab();
            }
            else
            {
                EditorDocumentTab next = _documentTabs.Tabs[
                    index + 1 < _documentTabs.Tabs.Count ? index + 1 : index - 1];
                ActivateDocumentTab(next);
            }

            if (ReferenceEquals(_documentTabs.ActiveTab, tab))
                return;
        }

        if (_documentTabs.Remove(tab))
        {
            tab.Audio?.Dispose();
            RefreshActiveDocumentTab();
        }
    }

    private async Task SaveAndCloseDocumentTabAsync(EditorDocumentTab tab)
    {
        // SaveCurrentAsync saves only the active document.
        if (!ReferenceEquals(_documentTabs.ActiveTab, tab))
            ActivateDocumentTab(tab);
        if (!ReferenceEquals(_documentTabs.ActiveTab, tab))
            return;

        bool saved = false;
        _tabCloseSavePending = true;
        try
        {
            saved = await SaveCurrentAsync(saveAs: false);
        }
        finally
        {
            _tabCloseSavePending = false;
        }

        // Canceled Save As, save failure or a newly-dirty tab stays open.
        if (saved && !tab.IsDirty && !_isClosed && _documentTabs.Tabs.Contains(tab))
            CloseDocumentTab(tab);
    }

    private void RefreshActiveDocumentTab()
    {
        // Tab order is always document order. Activation and close are
        // separate controls so clicking × cannot also activate the tab.
        DocumentTabsHost.Children.Clear();
        foreach (EditorDocumentTab tab in _documentTabs.Tabs)
        {
            bool isActive = ReferenceEquals(tab, _documentTabs.ActiveTab);
            var tabChrome = new Border
            {
                Margin = new Thickness(4, 0, 0, isActive ? 0 : 3),
                MaxWidth = 316,
                MinWidth = 110,
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Background = new SolidColorBrush(isActive
                    ? Color.FromRgb(41, 46, 55)
                    : Color.FromRgb(34, 38, 45)),
                BorderBrush = new SolidColorBrush(isActive
                    ? Color.FromRgb(70, 79, 93)
                    : Color.FromRgb(62, 69, 79)),
                BorderThickness = isActive
                    ? new Thickness(1, 1, 1, 0)
                    : new Thickness(1)
            };
            var controls = new DockPanel();

            var closeButton = new Button
            {
                Content = "×",
                ToolTip = $"Close {tab.DisplayName}",
                FontSize = 14,
                MinWidth = 25,
                MinHeight = 26,
                Padding = new Thickness(4, 0, 6, 0),
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(192, 199, 209))
            };
            closeButton.Click += (_, _) => CloseDocumentTab(tab);
            DockPanel.SetDock(closeButton, Dock.Right);
            controls.Children.Add(closeButton);

            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = tab.Header,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                ToolTip = tab.Document.SourcePath is null or "<synthetic>"
                    ? null : tab.Document.SourcePath,
                Tag = tab,
                MaxWidth = 290,
                MinWidth = 75,
                Margin = new Thickness(0),
                Padding = new Thickness(12, 2, 6, 2),
                Background = Brushes.Transparent,
                Foreground = isActive
                    ? (Brush)FindResource("TextBrush")
                    : new SolidColorBrush(Color.FromRgb(192, 199, 209))
            };
            button.Click += (_, _) => ActivateDocumentTab(tab);
            controls.Children.Add(button);

            tabChrome.Child = controls;
            DocumentTabsHost.Children.Add(tabChrome);
        }
    }

}
