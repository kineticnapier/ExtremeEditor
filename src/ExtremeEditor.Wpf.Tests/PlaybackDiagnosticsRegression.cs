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
