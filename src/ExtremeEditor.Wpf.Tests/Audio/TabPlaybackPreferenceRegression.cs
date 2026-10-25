using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class TabPlaybackPreferenceRegression
{
    internal static void Run()
    {
        string source = File.ReadAllText(Find("src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        if (!source.Contains("ApplySharedPlaybackPreferences(_audio)", StringComparison.Ordinal) ||
            !source.Contains("player.PlaybackSpeed = _playbackSpeed", StringComparison.Ordinal) ||
            !source.Contains("player.HitSoundsEnabled = HitSoundsToggle.IsChecked == true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("#37: restored tab audio does not match the shared playback UI preferences.");
        }
    }

    private static string Find(string relative)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
