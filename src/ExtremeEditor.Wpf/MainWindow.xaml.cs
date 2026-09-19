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

        LevelDocument level = LevelDocument.CreateSynthetic(4096);
        var index = new SpatialGridIndex(level.Positions);
        Viewport.SetLevel(level, index);

        StatusText.Text = $"WPF minimal viewport | {EditorVersion.Current} | synthetic 4096-floor level";
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
}
