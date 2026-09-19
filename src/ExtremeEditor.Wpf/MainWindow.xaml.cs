using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

public partial class MainWindow : Window
{
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

        FloorPreviewToggle.Checked += FloorPreviewChanged;
        FloorPreviewToggle.Unchecked += FloorPreviewChanged;
        Viewport.UseFloorPreview = FloorPreviewToggle.IsChecked == true;

        LevelDocument level = LevelDocument.CreateSynthetic(4096);
        var index = new SpatialGridIndex(level.Positions);
        Viewport.SetLevel(level, index);

        StatusText.Text = $"WPF floor/icon viewport | {EditorVersion.Current} | synthetic 4096-floor level | {Viewport.FloorAssetSummary} | {Viewport.IconAssetSummary}";
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
            totalWatch.Stop();

            Viewport.SetLevel(loaded.Document, loaded.Index);
            StatusText.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"read {loaded.Metrics.Read.TotalMilliseconds:N1} ms | " +
                $"parse {loaded.Metrics.Parse.TotalMilliseconds:N1} ms | " +
                $"path {loaded.Metrics.BuildPath.TotalMilliseconds:N1} ms | " +
                $"index {loaded.IndexTime.TotalMilliseconds:N1} ms | " +
                $"total {totalWatch.Elapsed.TotalMilliseconds:N1} ms";
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
