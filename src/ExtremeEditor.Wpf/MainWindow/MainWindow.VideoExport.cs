using System.IO;
using System.Windows;
using System.Windows.Input;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private CancellationTokenSource? _videoExportCancellation;
    private bool _videoExportInProgress;

    private async void ExportVideoClick(object sender, RoutedEventArgs e)
    {
        if (_videoExportInProgress ||
            _level is null ||
            _timingMap is null ||
            _hitSoundTimeline is null)
            return;

        if (_editor?.IsDirty == true ||
            string.IsNullOrWhiteSpace(_level.SourcePath) ||
            string.Equals(_level.SourcePath, "<synthetic>", StringComparison.Ordinal))
        {
            bool saved = await SaveCurrentAsync(saveAs: false);
            if (!saved ||
                _level is null ||
                _timingMap is null ||
                _hitSoundTimeline is null)
                return;
        }

        string chartPath = Path.GetFullPath(_level.SourcePath);
        if (!File.Exists(chartPath))
        {
            MessageBox.Show(
                this,
                "Save the chart before exporting video.",
                "Export Video",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string? ffmpegPath = FindFfmpegPath();
        if (ffmpegPath is null)
        {
            var ffmpegDialog = new OpenFileDialog
            {
                Title = "Locate ffmpeg.exe",
                Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (ffmpegDialog.ShowDialog(this) != true)
                return;
            ffmpegPath = ffmpegDialog.FileName;
        }

        string chartName = Path.GetFileNameWithoutExtension(chartPath);
        var saveDialog = new SaveFileDialog
        {
            Title = "Export Video",
            Filter = "MP4 video (*.mp4)|*.mp4",
            AddExtension = true,
            DefaultExt = ".mp4",
            FileName = chartName + ".mp4"
        };
        if (saveDialog.ShowDialog(this) != true)
            return;

        VideoExportSource source = await Task.Run(() => CreateVideoExportSnapshot(chartPath));

        var request = new VideoExportRequest(
            source,
            saveDialog.FileName,
            ffmpegPath,
            StartChartTimeSeconds: 0.0,
            EndChartTimeSeconds: source.TimingMap.Duration,
            FramesPerSecond: 60,
            Width: 1920,
            Height: 1080,
            SongVolumePercent: source.Level.SongVolumePercent,
            HitSoundsEnabled: HitSoundsToggle.IsChecked == true);

        using var cancellation = new CancellationTokenSource();
        _videoExportCancellation = cancellation;
        _videoExportInProgress = true;
        ExportVideoMenuItem.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;

        var progress = new Progress<VideoExportProgress>(value =>
        {
            string suffix = value.Total > 0
                ? $" {value.Completed:N0}/{value.Total:N0}"
                : value.Completed > 0
                    ? $" {value.Completed:N0}"
                    : string.Empty;
            StatusText.Text = $"Export Video — {value.Stage}{suffix}";
        });

        try
        {
            StatusText.Text = "Export Video — preparing…";
            var service = new VideoExportService();
            await service.ExportAsync(request, progress, cancellation.Token);
            StatusText.Text = $"Exported {Path.GetFileName(saveDialog.FileName)}";
            MessageBox.Show(
                this,
                $"Video export completed.\n\n{saveDialog.FileName}",
                "Export Video",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Video export cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Video export failed";
            MessageBox.Show(
                this,
                ex.ToString(),
                "Video export failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ExportVideoMenuItem.IsEnabled = true;
            _videoExportInProgress = false;
            _videoExportCancellation = null;
        }
    }

    private static VideoExportSource CreateVideoExportSnapshot(string chartPath)
    {
        LevelDocument level = WpfLevelLoader.Load(chartPath).Document;
        TimingMap timingMap = TimingMapBuilder.Build(level);
        HitSoundTimeline hitSoundTimeline = HitSoundTimelineBuilder.Build(level);
        return new VideoExportSource(level, timingMap, hitSoundTimeline, level.ResolveSongPath());
    }

    private static string? FindFfmpegPath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local))
            return local;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (string entry in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(entry, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }
}
