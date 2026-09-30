using System.ComponentModel;
using System.Threading;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private int _shutdownWatchdogStarted;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || Interlocked.Exchange(ref _shutdownWatchdogStarted, 1) != 0)
            return;

        // The WPF window can disappear before a blocking native/audio teardown
        // returns. Keep normal deterministic disposal, but do not leave dotnet run
        // hanging forever if a driver/native worker is stuck during shutdown.
        var watchdog = new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            Console.Error.WriteLine("[shutdown] cleanup exceeded 3 s; forcing process exit");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ExtremeEditor shutdown watchdog"
        };
        watchdog.Start();
    }

    internal void ShutdownNativeRenderer()
    {
        NativeViewport.ShutdownRenderer();
    }
}
