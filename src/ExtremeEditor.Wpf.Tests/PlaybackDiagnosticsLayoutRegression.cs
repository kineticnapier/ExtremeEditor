using System.Windows.Controls;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackDiagnosticsLayoutRegression
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            if (window.FindName("PlaybackDiagnosticsBar") is not ToolBar diagnosticsBar)
                throw new InvalidOperationException("MainWindow.PlaybackDiagnosticsBar is missing.");

            if (diagnosticsBar.Parent is not ToolBarTray)
                throw new InvalidOperationException("Playback diagnostics must live in the top ToolBarTray.");

            if (window.FindName("PlaybackDiagnosticsText") is not TextBlock diagnosticsText)
                throw new InvalidOperationException("MainWindow.PlaybackDiagnosticsText is missing.");

            if (!ReferenceEquals(diagnosticsText.Parent, diagnosticsBar))
                throw new InvalidOperationException("PlaybackDiagnosticsText must be hosted by the dedicated diagnostics toolbar.");

            if (!window.PlaybackDiagnosticsSnapshot.Contains("retained=", StringComparison.Ordinal))
                throw new InvalidOperationException("Playback diagnostics snapshot must include retained floor count.");
        }
        finally
        {
            window.Close();
        }
    }
}
