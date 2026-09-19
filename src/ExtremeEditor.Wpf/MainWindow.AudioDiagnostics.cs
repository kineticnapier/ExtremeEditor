using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private void HitSoundsToggleChanged(object sender, RoutedEventArgs e)
    {
        _audio.HitSoundsEnabled = HitSoundsToggle.IsChecked == true;
    }
}
