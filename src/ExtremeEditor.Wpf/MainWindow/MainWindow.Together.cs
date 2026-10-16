using System.Windows;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Non-modal Together panel shares the normal native chart canvas and
/// undoable EditorSession instead of creating a parallel editor. Ordinary
/// ADOFAI Ctrl+S is blocked while this collaboration view is active.
/// </summary>
public partial class MainWindow
{
    private TogetherWorkspaceWindow? _togetherPanel;
    private bool _togetherModeActive;

    internal LevelDocument? TogetherCurrentLevel => _level;
    internal EditorSession? TogetherCurrentEditor => EnsureEditorSession();

    private void OpenTogetherClick(object sender, RoutedEventArgs e)
    {
        if (_togetherPanel is { IsLoaded: true })
        {
            _togetherPanel.Activate();
            return;
        }

        var panel = new TogetherWorkspaceWindow(this);
        _togetherPanel = panel;
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_togetherPanel, panel))
                _togetherPanel = null;
        };
        panel.Show();
    }

    internal void ApplyTogetherDocument(LevelDocument chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (_editor?.IsDirty == true &&
            !ConfirmDiscardCurrentChanges())
            throw new OperationCanceledException("Editor reload cancelled.");

        StopPlayback();
        _audio.Unload();
        _silentPlaybackActive = false;
        _silentPlaybackPlaying = false;
        _playbackPreparationPending = false;
        _editorPlaybackRefreshTimer?.Stop();
        _editorPlaybackRefreshPending = false;
        _editorPlaybackRefreshDocument = null;

        _level = chart;
        _editor = new EditorSession(chart);
        _timingMap = TimingMapBuilder.Build(chart);
        _hitSoundTimeline = null;
        NativeViewport.SetLevel(chart);
        NativeViewport.SetPlaybackTimeline(_timingMap);
        _selection.SetFloorCount(chart.FloorCount);
        _selection.SetSelection([0], 0);
        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
        _togetherModeActive = true;

        RefreshInspector();
        CommandManager.InvalidateRequerySuggested();
        StatusText.Text =
            $"Together | floors {chart.FloorCount:N0} | edits are saved through Stage / Commit";
        UpdatePlaybackDisplay();
    }
}
