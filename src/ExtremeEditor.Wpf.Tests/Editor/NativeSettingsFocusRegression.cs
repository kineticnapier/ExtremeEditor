using System.IO;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeSettingsFocusRegression
{
    internal static void Run()
    {
        // A HwndHost must participate in WPF keyboard navigation, but Enter
        // must target the real Win32 child rather than only the WPF element.
        var viewport = new NativeLevelViewport();
        if (!viewport.Focusable)
            throw new InvalidOperationException("#69: native viewport is excluded from keyboard focus.");
        if (viewport.FocusEditorSurface())
            throw new InvalidOperationException("#69: focus succeeded without a native child HWND.");

        string nativeSource = ReadSource(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs");
        if (!nativeSource.Contains("SetFocus(childHwnd);", StringComparison.Ordinal) ||
            !nativeSource.Contains("GetFocus() == childHwnd", StringComparison.Ordinal) ||
            !nativeSource.Contains("childHwnd == nint.Zero || !IsVisible || !IsEnabled", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "#69: native focus must check HWND availability and verify the actual Win32 focus.");
        }

        string[] settingsHandlers =
        [
            "MainWindow.SongSettings.cs",
            "MainWindow.CameraSettings.cs",
            "MainWindow.TrackSettings.cs",
            "MainWindow.LevelMetadataSettings.cs"
        ];
        foreach (string filename in settingsHandlers)
        {
            string source = ReadSource("src/ExtremeEditor.Wpf/MainWindow/" + filename);
            if (!source.Contains("NativeViewport.FocusEditorSurface();", StringComparison.Ordinal) ||
                source.Contains("NativeViewport.Focus();", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "#69: Enter focus must use the native HWND in " + filename);
            }
        }
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("#69: source file not found: " + relativePath);
    }
}
