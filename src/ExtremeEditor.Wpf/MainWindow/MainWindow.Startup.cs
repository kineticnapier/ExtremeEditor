using System.IO;
using System.Windows.Threading;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    internal void ScheduleInitialOpen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        ContentRendered += OpenAfterContentRendered;
        return;

        void OpenAfterContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OpenAfterContentRendered;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _ = LoadLevelAsync(fullPath)));
        }
    }
}
