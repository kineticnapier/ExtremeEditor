using System.Windows;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = $"ExtremeEditor {EditorVersion.Current} — WPF";

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
