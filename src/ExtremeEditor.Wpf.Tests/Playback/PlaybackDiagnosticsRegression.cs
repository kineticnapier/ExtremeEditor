using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackDiagnosticsRegression
{
    public static void Run()
    {
        Type type = typeof(MainWindow);

        RequireProperty(type, "PlaybackUiRollingMilliseconds", typeof(double));
        RequireProperty(type, "PlaybackUiMaxMilliseconds", typeof(double));
        RequireProperty(type, "PlaybackDiagnosticsSnapshot", typeof(string));
        VerifySnapshotIncludesNativeFields();
    }

    private static void VerifySnapshotIncludesNativeFields()
    {
        var window = new MainWindow();
        try
        {
            string snapshot = window.PlaybackDiagnosticsSnapshot;
            string[] required =
            [
                "playback-ui fps=",
                "rolling=",
                "max=",
                "tickMaxGap=",
                "renderMaxGap=",
                "native="
            ];

            foreach (string token in required)
            {
                if (!snapshot.Contains(token, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Playback diagnostics snapshot is missing '{token}'. snapshot='{snapshot}'.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void RequireProperty(Type type, string name, Type propertyType)
    {
        PropertyInfo? property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property is null)
            throw new InvalidOperationException($"MainWindow.{name} is missing.");
        if (property.PropertyType != propertyType)
            throw new InvalidOperationException(
                $"MainWindow.{name} must be {propertyType.Name}, actual={property.PropertyType.Name}.");
    }
}
