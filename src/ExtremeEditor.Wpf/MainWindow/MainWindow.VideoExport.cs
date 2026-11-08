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

        // Claim the export before the first await (Save, snapshot preparation).
        // Otherwise a second click can start a second export while the first
        // is still preparing its chart, and both can overwrite cancellation state.
        _videoExportInProgress = true;
        ExportVideoMenuItem.IsEnabled = false;
        CancelVideoExportMenuItem.IsEnabled = false;

        try
        {
            await ExportVideoCoreAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Video export cancelled";
        }
        catch (Exception ex)
        {
            // Snapshot creation and Save are covered as well as FFmpeg/export.
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
            CancelVideoExportMenuItem.IsEnabled = false;
            _videoExportInProgress = false;
            _videoExportCancellation = null;
        }
    }

    private void CancelVideoExportClick(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? cancellation = _videoExportCancellation;
        if (cancellation is null)
            return;

        CancelVideoExportMenuItem.IsEnabled = false;
        if (TryCancelVideoExport(cancellation))
            StatusText.Text = "Video export — cancelling…";
    }

    // The async export can dispose its source before the UI continuation
    // clears the field. A queued Cancel command must not throw on that source.
    internal static bool TryCancelVideoExport(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return false;

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ExportVideoCoreAsync()
    {
        // Raw JSON edits are staged on the document tab until Save. Checking
        // EditorSession.IsDirty alone would export the old file contents.
        EditorDocumentTab? exportTab = _documentTabs.ActiveTab;
        if (exportTab is null)
            return;

        if (RequiresSaveBeforeVideoExport(exportTab))
        {
            bool saved = await SaveCurrentAsync(saveAs: false);
            if (!saved ||
                !ReferenceEquals(_documentTabs.ActiveTab, exportTab) ||
                exportTab.IsDirty ||
                _level is null ||
                _timingMap is null ||
                _hitSoundTimeline is null)
                return;
        }

        string chartPath = Path.GetFullPath(exportTab.Document.SourcePath);
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

        // Cancellation is available while the chart snapshot is being built,
        // not only after encoding begins. The individual builders are synchronous,
        // so cancellation is observed between stages rather than mid-stage.
        using var cancellation = new CancellationTokenSource();
        _videoExportCancellation = cancellation;
        CancelVideoExportMenuItem.IsEnabled = true;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Export Video — preparing…";
        bool hitSoundsEnabled = HitSoundsToggle.IsChecked == true;
        VideoExportSource source = await Task.Run(
            () => CreateVideoExportSnapshot(chartPath, cancellation.Token),
            cancellation.Token);

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
            HitSoundsEnabled: hitSoundsEnabled);

        // Progress<T> posts to the UI context. A queued callback may arrive
        // after ExportAsync has completed or cancellation has been requested.
        bool progressActive = true;
        // Capture the token before disposal: CancellationTokenSource.Token
        // throws after the source has been disposed, but queued progress can
        // still run after the export has finished.
        CancellationToken progressToken = cancellation.Token;
        var progress = new Progress<VideoExportProgress>(value =>
        {
            if (!ShouldReportVideoExportProgress(progressActive, progressToken))
                return;

            string suffix = value.Total > 0
                ? $" {value.Completed:N0}/{value.Total:N0}"
                : value.Completed > 0
                    ? $" {value.Completed:N0}"
                    : string.Empty;
            StatusText.Text = $"Export Video — {value.Stage}{suffix}";
        });

        var service = new VideoExportService();
        try
        {
            await service.ExportAsync(request, progress, cancellation.Token);
        }
        finally
        {
            progressActive = false;
        }

        StatusText.Text = $"Exported {Path.GetFileName(saveDialog.FileName)}";
        MessageBox.Show(
            this,
            $"Video export completed.\n\n{saveDialog.FileName}",
            "Export Video",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    internal static bool ShouldReportVideoExportProgress(
        bool progressActive, CancellationToken cancellationToken) =>
        progressActive && !cancellationToken.IsCancellationRequested;

    internal static bool RequiresSaveBeforeVideoExport(EditorDocumentTab tab) =>
        tab.IsDirty ||
        string.IsNullOrWhiteSpace(tab.Document.SourcePath) ||
        string.Equals(tab.Document.SourcePath, "<synthetic>", StringComparison.Ordinal);

    private static VideoExportSource CreateVideoExportSnapshot(
        string chartPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LevelDocument level = WpfLevelLoader.Load(chartPath).Document;
        cancellationToken.ThrowIfCancellationRequested();
        TimingMap timingMap = TimingMapBuilder.Build(level);
        cancellationToken.ThrowIfCancellationRequested();
        HitSoundTimeline hitSoundTimeline = HitSoundTimelineBuilder.Build(level);
        cancellationToken.ThrowIfCancellationRequested();
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

        return FindFfmpegInPathEntries(path);
    }

    // Windows PATH entries are sometimes enclosed in quotes. Path.Combine
    // treats those quotes as literal characters and misses an existing ffmpeg.
    internal static string? FindFfmpegInPathEntries(string path)
    {
        foreach (string entry in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string directory = entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"'
                    ? entry[1..^1]
                    : entry;
                string candidate = Path.Combine(directory, "ffmpeg.exe");
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
