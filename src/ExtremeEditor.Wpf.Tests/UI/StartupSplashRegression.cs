using System.IO;
using System.Runtime.CompilerServices;

namespace ExtremeEditor.Wpf.Tests;

internal static class StartupSplashRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        string root = Environment.CurrentDirectory;
        string splashPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "StartupSplashWindow.xaml");

        if (!File.Exists(splashPath))
        {
            throw new InvalidOperationException(
                "ExtremeEditor needs a dedicated startup splash instead of opening directly into the editor shell.");
        }

        string splash = File.ReadAllText(splashPath);
        if (!splash.Contains("LinearGradientBrush", StringComparison.Ordinal) ||
            !splash.Contains("ExtremeEditor", StringComparison.Ordinal) ||
            !splash.Contains("IsIndeterminate=\"True\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Startup splash must include branded gradient treatment, the ExtremeEditor title, and visible startup activity.");
        }

        string app = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "App.xaml.cs"));
        if (!app.Contains("StartupSplashWindow", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "App startup must actually present the dedicated splash before the main editor window.");
        }
    }
}
