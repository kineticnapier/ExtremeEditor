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
    private readonly Dictionary<EditorDocumentTab, DocumentTabVisual> _documentTabVisuals = [];
    private int _tabActivationGeneration;

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
        var editor = new EditorSession(document, clipboardStore: _editorClipboard);
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

        _ = ActivateDocumentTab(tab);
    }

    private bool CanSwitchDocumentTabs(bool allowDuringWindowClose = false)
    {
        if (_isClosed || _isLoading || _playbackPreparationPending ||
            _tabCloseSavePending || IsCleanPlay ||
            (_windowCloseConfirmationPending && !allowDuringWindowClose))
            return false;

        return true;
    }

    private async Task<bool> ActivateDocumentTab(
        EditorDocumentTab tab,
        Native.PreparedNativeLevel? preparedLevel = null,
        bool deferPlaybackTimeline = false,
        bool allowDuringWindowClose = false)
    {
        if (!CanSwitchDocumentTabs(allowDuringWindowClose) ||
            !_documentTabs.Tabs.Contains(tab) ||
            ReferenceEquals(_documentTabs.ActiveTab, tab))
            return ReferenceEquals(_documentTabs.ActiveTab, tab);

        int activationGeneration = ++_tabActivationGeneration;
        WpfPlaybackSetup? builtPlayback = null;
        TimingMap targetTimingMap = tab.TimingMap ??
            (builtPlayback = await Task.Run(() => WpfPlaybackSetupBuilder.Build(tab.Document))).TimingMap;
        HitSoundTimeline targetHitSoundTimeline = tab.HitSoundTimeline ??
            builtPlayback?.HitSoundTimeline ?? HitSoundTimelineBuilder.Build(tab.Document);

        Task<Native.PreparedNativeLevel> levelPreparation = preparedLevel is Native.PreparedNativeLevel readyLevel
            ? Task.FromResult(readyLevel)
            : NativeLoadPreparationCache.StartLevel(tab.Document);
        Task<Native.PreparedNativePlayback>? playbackPreparation = deferPlaybackTimeline
            ? null
            : NativeLoadPreparationCache.StartPlayback(tab.Document, targetTimingMap);
        var preparationWatch = Stopwatch.StartNew();
        try
        {
            if (playbackPreparation is null)
                await levelPreparation;
            else
                await Task.WhenAll(levelPreparation, playbackPreparation);
        }
        catch (Exception ex)
        {
            if (activationGeneration == _tabActivationGeneration && !_isClosed)
                StatusText.Text = $"Could not activate {tab.DisplayName}: {ex.Message}";
            return false;
        }
        double preparationWaitMs = preparationWatch.Elapsed.TotalMilliseconds;
        if (activationGeneration != _tabActivationGeneration || _isClosed ||
            !_documentTabs.Tabs.Contains(tab))
            return false;

        tab.TimingMap = targetTimingMap;
        tab.HitSoundTimeline = targetHitSoundTimeline;

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
            previous.SelectedEventSourceIndex =
                (EventList.SelectedItem as EventListItem)?.Action.SourceIndex;
        }

        _editorPlaybackRefreshTimer?.Stop();
        _editorPlaybackRefreshPending = false;
        _editorPlaybackRefreshDocument = null;

        _editor?.Changed -= ActiveEditorSessionChanged;
        _documentTabs.Activate(tab);

        _level = tab.Document;
        _editor = tab.Editor;
        _audio = tab.Audio ??= new AudioPlayer();
        ApplySharedPlaybackPreferences(_audio);
        _timingMap = targetTimingMap;
        _hitSoundTimeline = targetHitSoundTimeline;

        // NativeLevelViewport remains a single HWND. Restore the immutable
        // prepared snapshot for this document instead of rebuilding the entire
        // chart whenever the user returns to its tab.
        Native.PreparedNativeLevel nativeLevel = await levelPreparation;
        double levelWaitMs = preparationWaitMs;

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

            Native.PreparedNativePlayback playback = await playbackPreparation!;
            playbackWaitMs = preparationWaitMs;

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
        int? selectedEventSource = tab.SelectedEventSourceIndex;
        EventList.SelectedItem = null;
        RefreshInspector();
        if (selectedEventSource is int sourceIndex)
        {
            EventList.SelectedItem = EventList.Items.Cast<EventListItem>()
                .FirstOrDefault(item => item.Action.SourceIndex == sourceIndex);
        }
        RefreshMiscSettings();
        RefreshCameraSettings();
        RefreshTrackSettings();
        RefreshLevelMetadataSettings();
        RefreshActiveDocumentTab();
        UpdateEditorStatus();
        UpdatePlaybackDisplay();
        _discordPresence.SetEditing(tab.Document.FloorCount);
        CommandManager.InvalidateRequerySuggested();
        return true;
    }

    private void ApplySharedPlaybackPreferences(AudioPlayer player)
    {
        player.PlaybackSpeed = _playbackSpeed;
        player.HitSoundsEnabled = HitSoundsToggle.IsChecked == true;
    }

    private bool _tabCloseSavePending;

    private Task<bool> ConfirmUnsavedTabsBeforeCloseAsync() =>
        UnsavedDocumentCloseCoordinator.ConfirmAsync(
            _documentTabs.Tabs,
            PromptUnsavedDocumentForWindowClose,
            SaveDocumentForWindowCloseAsync);

    private UnsavedDocumentCloseChoice PromptUnsavedDocumentForWindowClose(EditorDocumentTab tab)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Save changes to {tab.DisplayName} before closing ExtremeEditor?",
            "Unsaved chart",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => UnsavedDocumentCloseChoice.Save,
            MessageBoxResult.No => UnsavedDocumentCloseChoice.Discard,
            _ => UnsavedDocumentCloseChoice.Cancel
        };
    }

    private async Task<bool> SaveDocumentForWindowCloseAsync(EditorDocumentTab tab)
    {
        if (!ReferenceEquals(_documentTabs.ActiveTab, tab))
            await ActivateDocumentTab(tab, allowDuringWindowClose: true);
        if (!ReferenceEquals(_documentTabs.ActiveTab, tab))
            return false;

        return await SaveCurrentAsync(saveAs: false);
    }

    private async void CloseDocumentTab(EditorDocumentTab tab)
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
                await ActivateDocumentTab(next);
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
            await ActivateDocumentTab(tab);
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
        foreach (EditorDocumentTab removed in _documentTabVisuals.Keys
                     .Where(tab => !_documentTabs.Tabs.Contains(tab)).ToArray())
        {
            DocumentTabsHost.Children.Remove(_documentTabVisuals[removed].Chrome);
            _documentTabVisuals.Remove(removed);
        }

        for (int index = 0; index < _documentTabs.Tabs.Count; index++)
        {
            EditorDocumentTab tab = _documentTabs.Tabs[index];
            if (!_documentTabVisuals.TryGetValue(tab, out DocumentTabVisual? visual))
            {
                visual = CreateDocumentTabVisual(tab);
                _documentTabVisuals.Add(tab, visual);
                DocumentTabsHost.Children.Insert(index, visual.Chrome);
            }
            else if (DocumentTabsHost.Children.IndexOf(visual.Chrome) != index)
            {
                DocumentTabsHost.Children.Remove(visual.Chrome);
                DocumentTabsHost.Children.Insert(index, visual.Chrome);
            }

            bool isActive = ReferenceEquals(tab, _documentTabs.ActiveTab);
            visual.Chrome.Margin = new Thickness(4, 0, 0, isActive ? 0 : 3);
            visual.Chrome.Background = new SolidColorBrush(isActive
                ? Color.FromRgb(41, 46, 55) : Color.FromRgb(34, 38, 45));
            visual.Chrome.BorderBrush = new SolidColorBrush(isActive
                ? Color.FromRgb(70, 79, 93) : Color.FromRgb(62, 69, 79));
            visual.Chrome.BorderThickness = isActive ? new Thickness(1, 1, 1, 0) : new Thickness(1);
            visual.Header.Text = tab.Header;
            visual.Activate.ToolTip = tab.Document.SourcePath is null or "<synthetic>"
                ? null : tab.Document.SourcePath;
            visual.Activate.Foreground = isActive
                ? (Brush)FindResource("TextBrush")
                : new SolidColorBrush(Color.FromRgb(192, 199, 209));
            visual.Close.ToolTip = $"Close {tab.DisplayName}";
        }
    }

    private DocumentTabVisual CreateDocumentTabVisual(EditorDocumentTab tab)
    {
        var header = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var activate = new Button
        {
            Content = header, Tag = tab, MaxWidth = 290, MinWidth = 75,
            Margin = new Thickness(0), Padding = new Thickness(12, 2, 6, 2),
            Background = Brushes.Transparent
        };
        activate.Click += (_, _) => _ = ActivateDocumentTab(tab);
        var close = new Button
        {
            Content = "×", FontSize = 14, MinWidth = 25, MinHeight = 26,
            Padding = new Thickness(4, 0, 6, 0), Margin = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(192, 199, 209))
        };
        close.Click += (_, _) => CloseDocumentTab(tab);
        DockPanel.SetDock(close, Dock.Right);
        var controls = new DockPanel();
        controls.Children.Add(close);
        controls.Children.Add(activate);
        var chrome = new Border
        {
            MaxWidth = 316, MinWidth = 110, CornerRadius = new CornerRadius(5, 5, 0, 0),
            Child = controls
        };
        return new DocumentTabVisual(chrome, activate, close, header);
    }

    private sealed record DocumentTabVisual(
        Border Chrome, Button Activate, Button Close, TextBlock Header);

}
