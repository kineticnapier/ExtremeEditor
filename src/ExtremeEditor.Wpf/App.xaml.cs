using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        window.Closed += (_, _) => window.ShutdownNativeRenderer();
        MainWindow = window;
        if (e.Args.Length > 0)
            window.ScheduleInitialOpen(e.Args[0]);
        window.Show();
    }
}
