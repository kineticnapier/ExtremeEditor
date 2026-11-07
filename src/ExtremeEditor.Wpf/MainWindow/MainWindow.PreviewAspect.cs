using System.Windows;
using System.Windows.Controls;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private void InitializePreviewAspect()
    {
        ApplyPreviewAspect(PreviewAspectSettings.Load(PreviewAspectSettings.CurrentPath));
    }

    private void PreviewAspectMenuClick(object sender, RoutedEventArgs e)
    {
        PreviewAspectMode? mode = (sender as MenuItem)?.Tag switch
        {
            "fit" => PreviewAspectMode.FitArea,
            "16:9" => PreviewAspectMode.Fixed16x9,
            _ => null
        };
        if (mode is not PreviewAspectMode selected)
            return;

        // Only the preview layout changes: the panel re-arranges the native
        // viewport, which resizes its renderer like a window resize, so
        // playback keeps running.
        ApplyPreviewAspect(selected);
        PreviewAspectSettings.Save(PreviewAspectSettings.CurrentPath, selected);
    }

    private void ApplyPreviewAspect(PreviewAspectMode mode)
    {
        PreviewAspectHost.Mode = mode;
        ViewPreviewAspectFitMenuItem.IsChecked = mode == PreviewAspectMode.FitArea;
        ViewPreviewAspect16x9MenuItem.IsChecked = mode == PreviewAspectMode.Fixed16x9;
    }
}
