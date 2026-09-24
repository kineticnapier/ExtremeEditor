using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

public partial class MainWindow : Window
{
    private AudioPlayer _audio = new();
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };

    private LevelDocument? _level;
    private TimingMap? _timingMap;
    private HitSoundTimeline? _hitSoundTimeline;
    private bool _isLoading;
    private bool _playbackPreparationPending;
    private bool _isClosed;
    private int _loadGeneration;

    // ADOFAI's editor can preview a chart even before a song file is assigned.
    // Keep a chart-time transport alongside the real audio transport so editor
    // playback is a level feature, not an AudioPlayer feature.
    private bool _silentPlaybackActive;
    private bool _silentPlaybackPlaying;
    private double _silentPlaybackChartTime;
    private long _silentPlaybackTimestamp;

    public MainWindow()
    {
        InitializeComponent();

        // Playback anomaly diagnostics are intentionally file-only. The console
        // is reserved for concise, one-shot load profiling so it remains usable
        // when opening pathological charts.
        _playbackDiagnosticLogger = new PlaybackDiagnosticLogger(
            TextWriter.Null,
            Path.Combine(Environment.CurrentDirectory, "playback-diagnostics.log"));
        CompositionTarget.Rendering += PlaybackCompositionRendering;

        Title = $"ExtremeEditor {EditorVersion.Current} — WPF";

        CommandBindings.Add(new CommandBinding(
            EditorCommands.Open,
            ExecuteOpen,
            CanExecuteOpen));
        CommandBindings.Add(new CommandBinding(
            EditorCommands.Frame,
            ExecuteFrame,
            CanExecuteFrame));
        CommandBindings.Add(new CommandBinding(
            EditorCommands.PlayPause,
            ExecutePlayPause,
            CanExecutePlayback));
        CommandBindings.Add(new CommandBinding(
            EditorCommands.Stop,
            ExecuteStop,
            CanExecutePlayback));

        FollowPlayerToggle.Checked += FollowPlayerToggleChanged;
        FollowPlayerToggle.Unchecked += FollowPlayerToggleChanged;
        NativeViewport.FollowPlayerChanged += NativeViewportFollowPlayerChanged;
        NativeViewport.FollowPlayer = FollowPlayerToggle.IsChecked == true;

        _playbackTimer.Tick += (_, _) => UpdatePlaybackDisplay();
        _playbackTimer.Start();

        // Startup should behave like an editor, not a renderer stress test.
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        _level = level;
        NativeViewport.SetLevel(level);
        NativeViewport.SetPlaybackTimeline(TimingMapBuilder.Build(level));
        _selection.SetFloorCount(level.FloorCount);
        _selection.SetSelection([0], 0);
        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);

        StatusText.Text = $"Native viewport | {EditorVersion.Current} | new 2-floor level";
        PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _loadGeneration++;
        _playbackTimer.Stop();
        _editorPlaybackRefreshTimer?.Stop();
        CompositionTarget.Rendering -= PlaybackCompositionRendering;
        NativeViewport.FollowPlayerChanged -= NativeViewportFollowPlayerChanged;
        _audio.Dispose();
        _playbackDiagnosticLogger.Dispose();
        base.OnClosed(e);
    }

    private async void ExecuteOpen(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardCurrentChanges())
        {
            e.Handled = true;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "ADOFAI levels (*.adofai)|*.adofai|All files (*.*)|*.*",
            Title = "Open ADOFAI level"
        };

        if (dialog.ShowDialog(this) == true)
            await LoadLevelAsync(dialog.FileName);

        e.Handled = true;
    }

    private void CanExecuteOpen(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading;
        e.Handled = true;
    }

    private async Task LoadLevelAsync(string path)
    {
        int generation = ++_loadGeneration;
        bool editorReady = false;
        try
        {
            _isLoading = true;
            _playbackPreparationPending = false;
            _editorPlaybackRefreshTimer?.Stop();
            _editorPlaybackRefreshPending = false;
            _editorPlaybackRefreshDocument = null;
            CommandManager.InvalidateRequerySuggested();
            Mouse.OverrideCursor = Cursors.Wait;
            StatusText.Text = "Loading…";

            var totalWatch = Stopwatch.StartNew();
            Console.WriteLine($"[load] begin file={Path.GetFileName(path)}");

            WpfLevelLoadResult loaded = await WpfLevelLoader.LoadAsync(path);
            Console.WriteLine(
                $"[load] source read={loaded.Metrics.Read.TotalMilliseconds:N1}ms " +
                $"parse={loaded.Metrics.Parse.TotalMilliseconds:N1}ms " +
                $"path={loaded.Metrics.BuildPath.TotalMilliseconds:N1}ms " +
                $"managedIndex={loaded.IndexTime.TotalMilliseconds:N1}ms");

            WpfPlaybackSetup playback = await Task.Run(
                () => WpfPlaybackSetupBuilder.Build(loaded.Document));
            Console.WriteLine(
                $"[load] playback timing={playback.TimingTime.TotalMilliseconds:N1}ms " +
                $"hitsounds={playback.HitSoundTime.TotalMilliseconds:N1}ms " +
                $"resolveSong={playback.ResolveSongTime.TotalMilliseconds:N1}ms");

            var phase = Stopwatch.StartNew();
            StopPlayback();
            _audio.Unload();
            TimeSpan audioUnload = phase.Elapsed;

            _level = loaded.Document;
            _timingMap = playback.TimingMap;
            _hitSoundTimeline = playback.HitSoundTimeline;

            Task<Native.PreparedNativeLevel> nativeLevelTask =
                NativeLoadPreparationCache.TryGetLevel(
                    loaded.Document,
                    out Task<Native.PreparedNativeLevel>? cachedLevelTask) &&
                cachedLevelTask is not null
                    ? cachedLevelTask
                    : NativeLoadPreparationCache.StartLevel(loaded.Document);
            Native.PreparedNativeLevel preparedNativeLevel = await nativeLevelTask;

            if (generation != _loadGeneration || _isClosed)
                return;

            NativeLevelLoadMetrics nativeMetrics =
                NativeViewport.SetPreparedLevelProfiled(loaded.Document, preparedNativeLevel);
            _selection.SetFloorCount(loaded.Document.FloorCount);
            _selection.SetSelection([0], 0);
            NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
            EnsureEditorSession();
            RefreshInspector();

            TimeSpan editorReadyTime = totalWatch.Elapsed;
            editorReady = true;
            _isLoading = false;
            _playbackPreparationPending = true;
            Mouse.OverrideCursor = null;
            CommandManager.InvalidateRequerySuggested();

            Console.WriteLine(
                $"[load] editor-ready floors={loaded.Document.FloorCount:N0} " +
                $"actions={loaded.Document.ActionCount:N0} " +
                $"nativeSnapshot={nativeMetrics.SnapshotBuild.TotalMilliseconds:N1}ms " +
                $"nativeUpload={nativeMetrics.Upload.TotalMilliseconds:N1}ms " +
                $"total={editorReadyTime.TotalMilliseconds:N1}ms");

            StatusText.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"editor ready {editorReadyTime.TotalMilliseconds:N1} ms | playback preparing…";
            UpdatePlaybackDisplay();

            Task<Native.PreparedNativePlayback> nativePlaybackTask =
                NativeLoadPreparationCache.StartPlayback(loaded.Document, playback.TimingMap);
            bool hitSoundsEnabled = HitSoundsToggle.IsChecked == true;
            Task<PreparedAudioLoad> audioPrepareTask = Task.Run(
                () => PreparedAudioLoad.Prepare(loaded.Document, playback, hitSoundsEnabled));

            PreparedAudioLoad preparedAudio;
            Native.PreparedNativePlayback preparedNativePlayback;
            try
            {
                await Task.WhenAll(audioPrepareTask, nativePlaybackTask);
                preparedAudio = await audioPrepareTask;
                preparedNativePlayback = await nativePlaybackTask;
            }
            catch (Exception ex)
            {
                if (generation == _loadGeneration && !_isClosed)
                {
                    _playbackPreparationPending = false;
                    CommandManager.InvalidateRequerySuggested();
                    Console.WriteLine(
                        $"[load] playback-failed total={totalWatch.Elapsed.TotalMilliseconds:N1}ms " +
                        $"exception={ex.GetType().Name} message={ex.Message}");
                    StatusText.Text =
                        $"{Path.GetFileName(path)} | editor ready {editorReadyTime.TotalMilliseconds:N1} ms | " +
                        $"playback preparation failed: {ex.Message}";
                }
                return;
            }

            if (generation != _loadGeneration ||
                _isClosed ||
                !ReferenceEquals(_level, loaded.Document) ||
                !ReferenceEquals(_timingMap, playback.TimingMap))
            {
                preparedAudio.Dispose();
                return;
            }

            NativePlaybackLoadMetrics nativePlaybackMetrics =
                NativeViewport.SetPreparedPlaybackTimelineProfiled(preparedNativePlayback);

            preparedAudio.Player.HitSoundsEnabled = HitSoundsToggle.IsChecked == true;
            AudioPlayer previousAudio = _audio;
            _audio = preparedAudio.Player;
            previousAudio.Dispose();

            _playbackPreparationPending = false;
            CommandManager.InvalidateRequerySuggested();

            Console.WriteLine(
                $"[load] audio unload={audioUnload.TotalMilliseconds:N1}ms " +
                $"configure={preparedAudio.ConfigureTime.TotalMilliseconds:N1}ms " +
                $"load={preparedAudio.LoadTime.TotalMilliseconds:N1}ms");
            if (_audio.IsLoaded)
            {
                AudioLoadMetrics audioMetrics = _audio.LastLoadMetrics;
                Console.WriteLine(
                    $"[load] audio-detail openReader={audioMetrics.OpenReader.TotalMilliseconds:N1}ms " +
                    $"renderAssets={audioMetrics.RenderHitSoundAssets.TotalMilliseconds:N1}ms " +
                    $"buildSchedule={audioMetrics.BuildHitSoundSchedule.TotalMilliseconds:N1}ms " +
                    $"renderChunks={audioMetrics.RenderHitSoundChunks.TotalMilliseconds:N1}ms " +
                    $"waveOutInit={audioMetrics.WaveOutInit.TotalMilliseconds:N1}ms " +
                    $"sourceHits={audioMetrics.SourceHitCount:N0} " +
                    $"scheduledHits={audioMetrics.ScheduledHitCount:N0} " +
                    $"chunks={audioMetrics.RenderedChunkCount:N0}");
            }

            TimeSpan playbackReadyTime = totalWatch.Elapsed;
            Console.WriteLine(
                $"[load] playback-ready nativeTimelineBuild={nativePlaybackMetrics.TimelineBuild.TotalMilliseconds:N1}ms " +
                $"nativeTimelineUpload={nativePlaybackMetrics.Upload.TotalMilliseconds:N1}ms " +
                $"total={playbackReadyTime.TotalMilliseconds:N1}ms");

            StatusText.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"editor {editorReadyTime.TotalMilliseconds:N1} ms | " +
                $"playback {playbackReadyTime.TotalMilliseconds:N1} ms | {preparedAudio.State}";
            UpdatePlaybackDisplay();
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || _isClosed)
                return;

            MessageBox.Show(this, ex.ToString(), "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = editorReady ? "Playback preparation failed" : "Open failed";
        }
        finally
        {
            if (generation == _loadGeneration && !_isClosed)
            {
                _isLoading = false;
                if (!editorReady)
                    _playbackPreparationPending = false;
                Mouse.OverrideCursor = null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string LoadSong(string? songPath)
    {
        if (songPath is null)
            return "audio unavailable";
        if (!File.Exists(songPath))
            return $"audio missing: {Path.GetFileName(songPath)}";

        try
        {
            _audio.Load(songPath);
            return $"audio {Path.GetFileName(songPath)}";
        }
        catch (Exception ex)
        {
            _audio.Unload();
            return $"audio load failed: {ex.Message}";
        }
    }

    private void ExecutePlayPause(object sender, ExecutedRoutedEventArgs e)
    {
        FlushEditorPlaybackRefresh();
        TogglePlayback();
        e.Handled = true;
    }

    private void ExecuteStop(object sender, ExecutedRoutedEventArgs e)
    {
        StopPlayback();
        e.Handled = true;
    }

    private void CanExecutePlayback(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_playbackPreparationPending &&
                       _level is not null &&
                       (_timingMap is not null || _editorPlaybackRefreshPending);
        e.Handled = true;
    }

    private void TogglePlayback()
    {
        FlushEditorPlaybackRefresh();
        if (_level is null || _timingMap is null)
            return;

        if (!_audio.IsLoaded)
        {
            ToggleSilentPlayback();
            return;
        }

        ClearSilentPlaybackState();

        if (_audio.IsPlaying)
        {
            _audio.Pause();
            PlayButton.Content = "Play";
            UpdatePlaybackDisplay();
            return;
        }

        if (_audio.IsStopped && _selection.PrimaryFloor >= 0)
        {
            double chartTime = _timingMap.GetEntryTime(_selection.PrimaryFloor);
            double audioTime = PlaybackClock.ChartToAudioTime(_level, chartTime);
            _audio.Seek(TimeSpan.FromSeconds(Math.Max(0.0, audioTime)));
        }

        if (_audio.IsStopped)
            ResetPlaybackUiDiagnostics();

        _audio.Play();
        PlayButton.Content = _audio.IsPlaying ? "Pause" : "Play";
        CommandManager.InvalidateRequerySuggested();
        UpdatePlaybackDisplay();
    }

    private void ToggleSilentPlayback()
    {
        if (_level is null || _timingMap is null)
            return;

        double chartRate = EditorChartRate;
        if (!_silentPlaybackActive)
        {
            _silentPlaybackChartTime = _selection.PrimaryFloor >= 0
                ? _timingMap.GetEntryTime(_selection.PrimaryFloor)
                : 0.0;
            _silentPlaybackChartTime = Math.Clamp(_silentPlaybackChartTime, 0.0, _timingMap.Duration);
            _silentPlaybackActive = true;
            _silentPlaybackPlaying = true;
            _silentPlaybackTimestamp = Stopwatch.GetTimestamp();
            ResetPlaybackUiDiagnostics();
        }
        else if (_silentPlaybackPlaying)
        {
            _silentPlaybackChartTime = CurrentSilentChartTime;
            _silentPlaybackPlaying = false;
        }
        else
        {
            if (_silentPlaybackChartTime >= _timingMap.Duration)
            {
                _silentPlaybackChartTime = _selection.PrimaryFloor >= 0
                    ? _timingMap.GetEntryTime(_selection.PrimaryFloor)
                    : 0.0;
            }
            _silentPlaybackTimestamp = Stopwatch.GetTimestamp();
            _silentPlaybackPlaying = true;
        }

        NativeViewport.SetPlaybackState(
            _silentPlaybackChartTime,
            chartRate,
            active: true,
            playing: _silentPlaybackPlaying);
        PlayButton.Content = _silentPlaybackPlaying ? "Pause" : "Play";
        CommandManager.InvalidateRequerySuggested();
        UpdatePlaybackDisplay();
    }

    private double EditorChartRate => Math.Max(0.000001, (_level?.PitchPercent ?? 100.0) * 0.01);

    private double CurrentSilentChartTime
    {
        get
        {
            if (!_silentPlaybackActive || !_silentPlaybackPlaying)
                return _silentPlaybackChartTime;

            return _silentPlaybackChartTime +
                   Stopwatch.GetElapsedTime(_silentPlaybackTimestamp).TotalSeconds * EditorChartRate;
        }
    }

    private void ClearSilentPlaybackState()
    {
        _silentPlaybackActive = false;
        _silentPlaybackPlaying = false;
        _silentPlaybackChartTime = 0.0;
        _silentPlaybackTimestamp = 0;
    }

    private void StopPlayback()
    {
        _audio.Stop();
        ClearSilentPlaybackState();
        NativeViewport.ClearPlayback();
        PlayButton.Content = "Play";
        PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdatePlaybackDisplay()
    {
        long started = BeginPlaybackUiSample();
        try
        {
            if (_level is null || _timingMap is null)
            {
                NativeViewport.ClearPlayback();
                if (ShouldRefreshPlaybackDiagnostics(started))
                    PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
                PlayButton.Content = "Play";
                return;
            }

            if (!_audio.IsLoaded)
            {
                UpdateSilentPlaybackDisplay(started);
                return;
            }

            double audioSeconds = _audio.Position.TotalSeconds;
            double chartTime = PlaybackClock.AudioToChartTime(_level, audioSeconds);
            double chartRate = EditorChartRate;
            NativeViewport.SetPlaybackState(
                chartTime,
                chartRate,
                active: !_audio.IsStopped,
                playing: _audio.IsPlaying);

            LogPlaybackAnomalies(chartTime);
            if (ShouldRefreshPlaybackDiagnostics(started))
            {
                PlaybackDiagnosticsText.Text =
                    $"A {_audio.Position:mm\\:ss\\.fff}/{_audio.Duration:mm\\:ss\\.fff} | " +
                    $"C {chartTime:F3}/{_timingMap.Duration:F3}s | {PlaybackDiagnosticsSnapshot}";
            }

            if (!_audio.IsPlaying)
                PlayButton.Content = "Play";
        }
        finally
        {
            EndPlaybackUiSample(started);
        }
    }

    private void UpdateSilentPlaybackDisplay(long sampleStarted)
    {
        if (_level is null || _timingMap is null || !_silentPlaybackActive)
        {
            NativeViewport.ClearPlayback();
            if (ShouldRefreshPlaybackDiagnostics(sampleStarted))
                PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
            PlayButton.Content = "Play";
            return;
        }

        double chartTime = CurrentSilentChartTime;
        if (_silentPlaybackPlaying && chartTime >= _timingMap.Duration)
        {
            StopPlayback();
            return;
        }

        chartTime = Math.Clamp(chartTime, 0.0, _timingMap.Duration);
        NativeViewport.SetPlaybackState(
            chartTime,
            EditorChartRate,
            active: true,
            playing: _silentPlaybackPlaying);

        if (ShouldRefreshPlaybackDiagnostics(sampleStarted))
        {
            PlaybackDiagnosticsText.Text =
                $"A --:--.--- | C {chartTime:F3}/{_timingMap.Duration:F3}s | silent preview | {PlaybackDiagnosticsSnapshot}";
        }

        PlayButton.Content = _silentPlaybackPlaying ? "Pause" : "Play";
    }

    private void ExecuteFrame(object sender, ExecutedRoutedEventArgs e)
    {
        NativeViewport.FrameAll();
        e.Handled = true;
    }

    private void CanExecuteFrame(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = true;
    }

    private void FollowPlayerToggleChanged(object sender, RoutedEventArgs e)
    {
        NativeViewport.FollowPlayer = FollowPlayerToggle.IsChecked == true;
    }

    private void NativeViewportFollowPlayerChanged(bool enabled)
    {
        FollowPlayerToggle.IsChecked = enabled;
    }
}
