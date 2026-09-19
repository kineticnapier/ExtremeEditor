using System.Reflection;
using System.Windows.Threading;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PerformanceRegression
{
    public static void Run()
    {
        VerifyPlaybackTimerIdleByDefault();
    }

    private static void VerifyPlaybackTimerIdleByDefault()
    {
        var window = new MainWindow();
        try
        {
            FieldInfo timerField = typeof(MainWindow).GetField(
                "_playbackTimer",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow._playbackTimer is missing.");

            if (timerField.GetValue(window) is not DispatcherTimer timer)
                throw new InvalidOperationException("MainWindow._playbackTimer must be a DispatcherTimer.");

            if (timer.IsEnabled)
                throw new InvalidOperationException("Playback timer must stay stopped while playback is idle.");
        }
        finally
        {
            window.Close();
        }
    }
}
