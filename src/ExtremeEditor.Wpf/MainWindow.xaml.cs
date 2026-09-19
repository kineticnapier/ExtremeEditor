using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

    public MainWindow()
    {
        InitializeComponent();

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

        _playbackTimer.Tick += (_, _) => UpdatePlaybackDisplay();
        _playbackTimer.Start();

        LevelDocument level = LevelDocument.CreateSynthetic(4096);
        var index = new SpatialGridIndex(level.Positions);
        _level = level;
        Viewport.SetLevel(level, index);

        StatusText.Text = $"WPF floor/icon viewport | {EditorVersion.Current} | synthetic 4096-floor level | {Viewport.FloorAssetSummary} | {Viewport.IconAssetSummary}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _playbackTimer.Stop();
        _audio.Dispose();
        base.OnClosed(e);
    }

    private void ExecuteOpen(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ADOFAI levels (*.adofai)|*.adofai|All files (*.*)|*.*",
            Title = "Open ADOFAI level"
        };

        if (dialog.ShowDialog(this) == true)
            LoadLevel(dialog.FileName);

        e.Handled = true;
    }

    private void CanExecuteOpen(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = true;
    }

    private void LoadLevel(string path)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            StatusText.Text = "Loading…";

            var totalWatch = Stopwatch.StartNew();
            WpfLevelLoadResult loaded = WpfLevelLoader.Load(path);
            WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(loaded.Document);

            StopPlayback();
            _audio.Unload();
            _audio.ConfigureHitSounds(
                loaded.Document,
                playback.TimingMap,
                playback.HitSoundTimeline);

            string audioState = LoadSong(playback.SongPath);

            _level = loaded.Document;
            _timingMap = playback.TimingMap;
            _hitSoundTimeline = playback.HitSoundTimeline;
            Viewport.SetLevel(loaded.Document, loaded.Index);
            totalWatch.Stop();

            StatusText.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"read {loaded.Metrics.Read.TotalMilliseconds:N1} ms | " +
                $"parse {loaded.Metrics.Parse.TotalMilliseconds:N1} ms | " +
                $"path {loaded.Metrics.BuildPath.TotalMilliseconds:N1} ms | " +
                $"index {loaded.IndexTime.TotalMilliseconds:N1} ms | " +
                $"total {totalWatch.Elapsed.TotalMilliseconds:N1} ms | {audioState}";

            CommandManager.InvalidateRequerySuggested();
            UpdatePlaybackDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Open failed";
        }
        finally
        {
            Mouse.OverrideCursor = null;
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
        e.CanExecute = _level is not null && _timingMap is not null && _audio.IsLoaded;
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

        _audio.Play();
        PlayButton.Content = _audio.IsPlaying ? "Pause" : "Play";
        CommandManager.InvalidateRequerySuggested();
        UpdatePlaybackDisplay();
    }

    private void StopPlayback()
    {
        _audio.Stop();
        PlayButton.Content = "Play";
        PlaybackText.Text = "--:--.---";
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdatePlaybackDisplay()
    {
        if (_level is null || _timingMap is null || !_audio.IsLoaded)
        {
            PlaybackText.Text = "--:--.---";
            PlayButton.Content = "Play";
            return;
        }

        double audioSeconds = _audio.Position.TotalSeconds;
        double chartTime = PlaybackClock.AudioToChartTime(_level, audioSeconds);
        PlaybackText.Text =
            $"A {_audio.Position:mm\\:ss\\.fff}/{_audio.Duration:mm\\:ss\\.fff} | " +
            $"C {chartTime:F3}/{_timingMap.Duration:F3}s";

        if (!_audio.IsPlaying)
            PlayButton.Content = "Play";
    }

    private void ExecuteFrame(object sender, ExecutedRoutedEventArgs e)
    {
        Viewport.FrameAll();
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
}
