using System.Windows;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"ExtremeEditor {EditorVersion.Current} — WPF";
    }
}
