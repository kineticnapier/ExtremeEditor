using System.Windows.Input;
using System.Windows.Threading;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private DispatcherTimer? _editorPlaybackRefreshTimer;
    private bool _editorPlaybackRefreshPending;
    private LevelDocument? _editorPlaybackRefreshDocument;

    private void RefreshEditorInteractionAfterMutation(
        int preferredPrimary,
        IEnumerable<int>? preferredSelection = null)
    {
        if (_level is null)
            return;

        if (_audio.IsPlaying || !_audio.IsStopped)
            StopPlayback();
        else
        {
            Viewport.SetPlaybackPose(null);
            NativeViewport.ClearPlayback();
        }

        int maxFloor = Math.Max(0, _level.FloorCount - 1);
        int[] selection = (preferredSelection?.ToArray() ?? [preferredPrimary])
            .Select(floor => Math.Clamp(floor, 0, maxFloor))
            .Distinct()
            .ToArray();
        int primary = Math.Clamp(preferredPrimary, 0, maxFloor);

        // Geometry/selection are the interactive path. Keep these synchronous so the
        // newly-created floor appears immediately, but defer playback model rebuilds.
        var index = new SpatialGridIndex(_level.Positions);
        Viewport.SetLevel(_level, index, preserveView: true);
        Viewport.SetSelection(selection, primary);
        NativeViewport.SetLevel(_level);
        NativeViewport.SetSelection(selection, primary);

        // Old timing must never be consumed together with the edited document.
        _timingMap = null;
        _hitSoundTimeline = null;
        ScheduleEditorPlaybackRefresh();

        RefreshInspector();
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ScheduleEditorPlaybackRefresh()
    {
        if (_level is null)
            return;

        _editorPlaybackRefreshPending = true;
        _editorPlaybackRefreshDocument = _level;

        if (_editorPlaybackRefreshTimer is null)
        {
            _editorPlaybackRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _editorPlaybackRefreshTimer.Tick += (_, _) => FlushEditorPlaybackRefresh();
        }

        _editorPlaybackRefreshTimer.Stop();
        _editorPlaybackRefreshTimer.Start();
    }

    private void FlushEditorPlaybackRefresh()
    {
        _editorPlaybackRefreshTimer?.Stop();
        if (!_editorPlaybackRefreshPending)
            return;

        _editorPlaybackRefreshPending = false;
        LevelDocument? document = _editorPlaybackRefreshDocument;
        _editorPlaybackRefreshDocument = null;
        if (document is null || !ReferenceEquals(document, _level))
            return;

        WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(document);
        _timingMap = playback.TimingMap;
        _hitSoundTimeline = playback.HitSoundTimeline;
        _audio.ConfigureHitSounds(document, _timingMap, _hitSoundTimeline);
        NativeViewport.SetPlaybackTimeline(_timingMap);
        CommandManager.InvalidateRequerySuggested();
    }
}
