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
    private readonly AudioPlayer _audio = new();
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };

    private LevelDocument? _level;
    private TimingMap? _timingMap;
    private HitSoundTimeline? _hitSoundTimeline;
    private bool _isLoading;

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

        FloorPreviewToggle.Checked += FloorPreviewChanged;
        FloorPreviewToggle.Unchecked += FloorPreviewChanged;
        Viewport.UseFloorPreview = FloorPreviewToggle.IsChecked == true;

        FollowPlayerToggle.Checked += FollowPlayerToggleChanged;
        FollowPlayerToggle.Unchecked += FollowPlayerToggleChanged;
        Viewport.FollowPlayerChanged += ViewportFollowPlayerChanged;
        NativeViewport.FollowPlayerChanged += NativeViewportFollowPlayerChanged;
        Viewport.FollowPlayer = FollowPlayerToggle.IsChecked == true;
        NativeViewport.FollowPlayer = FollowPlayerToggle.IsChecked == true;
        NativeViewport.SelectedFloorChanged += NativeViewportSelectedFloorChanged;

        _playbackTimer.Tick += (_, _) => UpdatePlaybackDisplay();
        _playbackTimer.Start();

        // Startup should behave like an editor, not a renderer stress test.
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        var index = new SpatialGridIndex(level.Positions);
        _level = level;
        Viewport.SetLevel(level, index);
        NativeViewport.SetLevel(level);
        NativeViewport.SetPlaybackTimeline(TimingMapBuilder.Build(level));

        StatusText.Text = $"WPF floor/icon viewport | {EditorVersion.Current} | new 2-floor level | {Viewport.FloorAssetSummary} | {Viewport.IconAssetSummary}";
        PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _playbackTimer.Stop();
        _editorPlaybackRefreshTimer?.Stop();
        CompositionTarget.Rendering -= PlaybackCompositionRendering;
        Viewport.FollowPlayerChanged -= ViewportFollowPlayerChanged;
        NativeViewport.FollowPlayerChanged -= NativeViewportFollowPlayerChanged;
        NativeViewport.SelectedFloorChanged -= NativeViewportSelectedFloorChanged;
        Viewport.ShutdownRasterWorker();
        _audio.Dispose();
        _playbackDiagnosticLogger.Dispose();
        base.OnClosed(e);
    }

    private async void ExecuteOpen(object sender, ExecutedRoutedEventArgs e)
    {
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
        try
        {
            _isLoading = true;
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

            // Timing and hit-sound setup are pure CPU work. Keep them off the UI
            // thread as well so a multi-million-floor chart stays responsive while
            // its playback model is prepared.
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

            phase.Restart();
            _audio.ConfigureHitSounds(
                loaded.Document,
                playback.TimingMap,
                playback.HitSoundTimeline);
            TimeSpan audioConfigure = phase.Elapsed;

            phase.Restart();
            string audioState = LoadSong(playback.SongPath);
            TimeSpan audioLoad = phase.Elapsed;
            Console.WriteLine(
                $"[load] audio unload={audioUnload.TotalMilliseconds:N1}ms " +
                $"configure={audioConfigure.TotalMilliseconds:N1}ms " +
                $"load={audioLoad.TotalMilliseconds:N1}ms");
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

            _level = loaded.Document;
            _timingMap = playback.TimingMap;
            _hitSoundTimeline = playback.HitSoundTimeline;

            phase.Restart();
            Viewport.SetLevel(loaded.Document, loaded.Index);
            TimeSpan wpfSetLevel = phase.Elapsed;

            NativeLevelLoadMetrics nativeMetrics = NativeViewport.SetLevelProfiled(loaded.Document);
            NativePlaybackLoadMetrics nativePlaybackMetrics = NativeViewport.SetPlaybackTimelineProfiled(playback.TimingMap);
            Console.WriteLine(
                $"[load] viewport wpfSetLevel={wpfSetLevel.TotalMilliseconds:N1}ms " +
                $"nativeSnapshot={nativeMetrics.SnapshotBuild.TotalMilliseconds:N1}ms " +
                $"nativeUpload={nativeMetrics.Upload.TotalMilliseconds:N1}ms " +
                $"nativeTimelineBuild={nativePlaybackMetrics.TimelineBuild.TotalMilliseconds:N1}ms " +
                $"nativeTimelineUpload={nativePlaybackMetrics.Upload.TotalMilliseconds:N1}ms");

            totalWatch.Stop();
            Console.WriteLine(
                $"[load] done floors={loaded.Document.FloorCount:N0} " +
                $"actions={loaded.Document.ActionCount:N0} total={totalWatch.Elapsed.TotalMilliseconds:N1}ms");

            StatusText.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"read {loaded.Metrics.Read.TotalMilliseconds:N1} ms | " +
                $"parse {loaded.Metrics.Parse.TotalMilliseconds:N1} ms | " +
                $"path {loaded.Metrics.BuildPath.TotalMilliseconds:N1} ms | " +
                $"index {loaded.IndexTime.TotalMilliseconds:N1} ms | " +
                $"total {totalWatch.Elapsed.TotalMilliseconds:N1} ms | {audioState}";

            UpdatePlaybackDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Open failed";
        }
        finally
        {
            _isLoading = false;
            Mouse.OverrideCursor = null;
            CommandManager.InvalidateRequerySuggested();
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
        e.CanExecute = _level is not null &&
                       (_timingMap is not null || _editorPlaybackRefreshPending) &&
                       _audio.IsLoaded;
        e.Handled = true;
    }

    private void TogglePlayback()
    {
        if (_level is null || _timingMap is null || !_audio.IsLoaded)
            return;

        if (_audio.IsPlaying)
        {
            _audio.Pause();
            PlayButton.Content = "Play";
            UpdatePlaybackDisplay();
            return;
        }

        if (_audio.IsStopped && Viewport.SelectedFloor >= 0)
        {
            double chartTime = _timingMap.GetEntryTime(Viewport.SelectedFloor);
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

    private void StopPlayback()
    {
        _audio.Stop();
        Viewport.SetPlaybackPose(null);
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
            if (_level is null || _timingMap is null || !_audio.IsLoaded)
            {
                Viewport.SetPlaybackPose(null);
                NativeViewport.ClearPlayback();
                if (ShouldRefreshPlaybackDiagnostics(started))
                    PlaybackDiagnosticsText.Text = $"A --:--.--- | C -- | {PlaybackDiagnosticsSnapshot}";
                PlayButton.Content = "Play";
                return;
            }

            double audioSeconds = _audio.Position.TotalSeconds;
            double chartTime = PlaybackClock.AudioToChartTime(_level, audioSeconds);
            WpfPlaybackPresenter.Update(
                Viewport,
                _level,
                _timingMap,
                audioSeconds,
                _audio.IsStopped);

            double chartRate = Math.Max(0.000001, _level.PitchPercent * 0.01);
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

    private void ExecuteFrame(object sender, ExecutedRoutedEventArgs e)
    {
        Viewport.FrameAll();
        NativeViewport.FrameAll();
        e.Handled = true;
    }

    private void CanExecuteFrame(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = true;
    }

    private void FloorPreviewChanged(object sender, RoutedEventArgs e)
    {
        Viewport.UseFloorPreview = FloorPreviewToggle.IsChecked == true;
    }

    private void FollowPlayerToggleChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = FollowPlayerToggle.IsChecked == true;
        Viewport.FollowPlayer = enabled;
        NativeViewport.FollowPlayer = enabled;
    }

    private void ViewportFollowPlayerChanged(object? sender, EventArgs e)
    {
        bool enabled = Viewport.FollowPlayer;
        FollowPlayerToggle.IsChecked = enabled;
        NativeViewport.FollowPlayer = enabled;
    }

    private void NativeViewportFollowPlayerChanged(bool enabled)
    {
        NativeViewport.FollowPlayer = enabled;
        Viewport.FollowPlayer = enabled;
        FollowPlayerToggle.IsChecked = enabled;
    }

    private void NativeViewportSelectedFloorChanged(int floor)
    {
        Viewport.SetSelectedFloorFromExternal(floor);
    }

    private void NativeViewportToggleChanged(object sender, RoutedEventArgs e)
    {
        bool useNative = NativeViewportToggle.IsChecked == true;
        NativeViewport.Visibility = useNative ? Visibility.Visible : Visibility.Collapsed;
        Viewport.Visibility = useNative ? Visibility.Collapsed : Visibility.Visible;

        StatusText.Text = useNative
            ? $"Native Direct2D/D3D11 playback viewport | {EditorVersion.Current} | independent native render thread"
            : $"WPF viewport | {EditorVersion.Current}";
    }
}
