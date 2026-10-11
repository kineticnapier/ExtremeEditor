using System.Windows;
using System.Windows.Threading;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new StartupSplashWindow();
        splash.Show();

        // Let WPF paint the splash before MainWindow performs renderer/UI startup.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                var window = new MainWindow();
                window.Closed += (_, _) => window.ShutdownNativeRenderer();
                MainWindow = window;
                if (e.Args.Length > 0)
                    window.ScheduleInitialOpen(e.Args[0]);
                window.Show();
                splash.Close();
            }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownDiagnostics.Begin("Application.OnExit");
        base.OnExit(e);
        ShutdownDiagnostics.Complete("Application.OnExit");
    }
}
