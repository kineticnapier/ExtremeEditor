using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length > 0)
            window.ScheduleInitialOpen(e.Args[0]);
        window.Show();
    }
}
