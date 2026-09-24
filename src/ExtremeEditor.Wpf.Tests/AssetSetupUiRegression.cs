using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class AssetSetupUiRegression
{
    public static void Run()
    {
        Type mainWindowType = typeof(MainWindow);
        RequireField(mainWindowType, "SetupAssetsButton");
        RequireField(mainWindowType, "BrowseAdoFaiButton");
        RequireField(mainWindowType, "AdoFaiPathText");

        MethodInfo setupClick = RequireHandler(mainWindowType, "SetupAssetsClick");
        if (setupClick.GetCustomAttribute<AsyncStateMachineAttribute>() is null)
            throw new InvalidOperationException("SetupAssetsClick must be async so extraction cannot block the WPF UI thread.");

        RequireHandler(mainWindowType, "BrowseAdoFaiClick");
        RequireMethod(mainWindowType, "RefreshAssetSetupUi");

        PropertyInfo extractorPathProperty = typeof(AssetSetupService).GetProperty(
            "DefaultExtractorPath",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AssetSetupService.DefaultExtractorPath is missing.");

        if (extractorPathProperty.GetValue(null) is not string extractorPath ||
            !extractorPath.EndsWith("ExtremeEditor.AssetExtractor.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DefaultExtractorPath must resolve the standalone ExtremeEditor.AssetExtractor executable.");
        }

        Assembly wpfAssembly = mainWindowType.Assembly;
        Type settingsType = wpfAssembly.GetType("ExtremeEditor.Wpf.AdoFaiInstallationSettings")
            ?? throw new InvalidOperationException("AdoFaiInstallationSettings does not exist yet.");

        MethodInfo loadMethod = settingsType.GetMethod(
            "LoadSavedPath",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("AdoFaiInstallationSettings.LoadSavedPath(string settingsDirectory) is missing.");

        MethodInfo saveMethod = settingsType.GetMethod(
            "SavePath",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("AdoFaiInstallationSettings.SavePath(string settingsDirectory, string gameRoot) is missing.");

        string settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-Wpf-AssetSetupUi-{Guid.NewGuid():N}");
        string expectedPath = Path.Combine(settingsDirectory, "ADOFAI Custom Location");

        try
        {
            saveMethod.Invoke(null, [settingsDirectory, expectedPath]);
            string? actualPath = loadMethod.Invoke(null, [settingsDirectory]) as string;
            if (actualPath is null ||
                !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Manually selected ADOFAI path must persist for the next setup run.");
            }
        }
        finally
        {
            if (Directory.Exists(settingsDirectory))
            {
                try
                {
                    Directory.Delete(settingsDirectory, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private static void RequireField(Type type, string name)
    {
        if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is null)
            throw new InvalidOperationException($"MainWindow XAML field '{name}' is missing.");
    }

    private static MethodInfo RequireHandler(Type type, string name)
    {
        return type.GetMethod(
                   name,
                   BindingFlags.Instance | BindingFlags.NonPublic,
                   binder: null,
                   types: [typeof(object), typeof(RoutedEventArgs)],
                   modifiers: null)
               ?? throw new InvalidOperationException($"MainWindow.{name}(object, RoutedEventArgs) is missing.");
    }

    private static MethodInfo RequireMethod(Type type, string name)
    {
        return type.GetMethod(
                   name,
                   BindingFlags.Instance | BindingFlags.NonPublic,
                   binder: null,
                   types: Type.EmptyTypes,
                   modifiers: null)
               ?? throw new InvalidOperationException($"MainWindow.{name}() is missing.");
    }
}
