using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeDecorationFailureRegression
{
    internal static void Run()
    {
        string session = Read("src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs");
        string viewport = Read("src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs");
        string window = Read("src/ExtremeEditor.Wpf/MainWindow.xaml.cs");

        Require(session.Contains("LastDecorationAssetFailures", StringComparison.Ordinal) &&
                session.Contains("decorationAssetFailures.Add(asset.ImagePath)", StringComparison.Ordinal),
            "interactive upload must collect failed image paths without skipping healthy assets");
        Require(viewport.Contains("DecorationAssetLoadFailed", StringComparison.Ordinal),
            "native viewport must surface collected decoration failures");
        Require(window.Contains("NativeViewport.DecorationAssetLoadFailed", StringComparison.Ordinal) &&
                window.Contains("Decoration images failed to load", StringComparison.Ordinal),
            "main window must show the missing-decoration warning to the user");
    }

    private static string Read(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("RED setup failure: " + relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
