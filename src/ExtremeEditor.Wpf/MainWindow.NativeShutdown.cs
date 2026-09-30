using System.Threading;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private int _shutdownWatchdogStarted;

    private void StartShutdownWatchdog()
    {
        if (Interlocked.Exchange(ref _shutdownWatchdogStarted, 1) != 0)
            return;

        // The WPF window can disappear before a blocking native/audio teardown
        // returns. Keep normal deterministic disposal, but do not leave dotnet run
        // hanging forever if a driver/native worker is stuck during shutdown.
        var watchdog = new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            ShutdownDiagnostics.WriteOnce(
                "ShutdownWatchdog.ForceExit",
                $"lastCompleted={ShutdownDiagnostics.LastCompletedPhase}");
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
        ShutdownDiagnostics.Begin("MainWindow.ShutdownNativeRenderer");
        NativeViewport.ShutdownRenderer();
        ShutdownDiagnostics.Complete("MainWindow.ShutdownNativeRenderer");
    }
}
