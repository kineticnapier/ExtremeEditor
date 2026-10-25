namespace ExtremeEditor.Core.Tests;

internal static class CollaborationRemovalRegression
{
    internal static void Run()
    {
        string root = FindRepositoryRoot();
        string[] forbiddenPaths =
        [
            "cloudflare/together-bundle/package.json",
            "cloudflare/together-bundle/src/index.mjs",
            "run-together-test.ps1",
            "run-together-public-e2e.ps1",
            "src/ExtremeEditor.Audio/TogetherAudioWaveClipWriter.cs",
            "src/ExtremeEditor.Core/ITogetherAudioClipWriter.cs",
            "src/ExtremeEditor.Core/SegmentGitWorkspace.cs",
            "src/ExtremeEditor.Core/TimeFirstSegmentGitWorkspace.cs",
            "src/ExtremeEditor.Core/TogetherPackageExporter.cs",
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.Together.cs",
            "src/ExtremeEditor.Wpf/Dialogs/TogetherWorkspaceWindow.xaml",
            "src/ExtremeEditor.Wpf/Dialogs/TogetherWorkspaceWindow.xaml.cs",
            "src/ExtremeEditor.Wpf/Services/TogetherWorkspaceController.cs",
            "src/ExtremeEditor.Wpf/Controls/TogetherMusicTimelineControl.cs"
        ];

        string[] remaining = forbiddenPaths
            .Where(path => File.Exists(Path.Combine(root, path)) ||
                           Directory.Exists(Path.Combine(root, path)))
            .ToArray();
        if (remaining.Length > 0)
        {
            throw new InvalidOperationException(
                "Collaboration-only assets remain: " + string.Join(", ", remaining));
        }

        AssertDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Wpf/MainWindow.xaml"),
            "TogetherMenuItem",
            "the main menu still exposes collaboration");
        AssertDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Wpf/MainWindow/MainWindow.Editor.cs"),
            "_togetherModeActive",
            "normal Save/Save As still depends on collaboration mode");
        AssertDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"),
            "_togetherModeActive",
            "normal multi-tab creation still depends on collaboration mode");

        AssertTreeDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Core"),
            ["Together", "TimeFirst"],
            "Core still contains collaboration-only types");
        AssertTreeDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Audio"),
            ["Together", "TimeFirst"],
            "Audio still contains collaboration-only types");
        AssertTreeDoesNotContain(
            Path.Combine(root, "src/ExtremeEditor.Wpf"),
            ["Together", "TimeFirst"],
            "WPF still contains a collaboration entry point");

        string[] retiredTestSwitches =
        [
            "EXTREMEEDITOR_TOGETHER_",
            "EXTREMEEDITOR_TIME_",
            "EXTREMEEDITOR_SEGMENT_",
            "EXTREMEEDITOR_AUDIO_CUT_ONLY"
        ];
        AssertDoesNotContainAny(
            Path.Combine(root, "src/ExtremeEditor.Core.Tests/Program.cs"),
            retiredTestSwitches,
            "Core test runner still registers a collaboration-only suite");
        AssertDoesNotContainAny(
            Path.Combine(root, "src/ExtremeEditor.Audio.Tests/Program.cs"),
            retiredTestSwitches,
            "Audio test runner still registers a collaboration-only suite");
        AssertDoesNotContainAny(
            Path.Combine(root, "src/ExtremeEditor.Wpf.Tests/Program.cs"),
            retiredTestSwitches,
            "WPF test runner still registers a collaboration-only suite");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the ExtremeEditor repository root.");
    }

    private static void AssertDoesNotContain(string path, string text, string message)
    {
        if (File.ReadAllText(path).Contains(text, StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private static void AssertDoesNotContainAny(
        string path,
        IReadOnlyList<string> texts,
        string message)
    {
        string source = File.ReadAllText(path);
        string? found = texts.FirstOrDefault(text =>
            source.Contains(text, StringComparison.Ordinal));
        if (found is not null)
            throw new InvalidOperationException($"{message}: {found} in {path}");
    }

    private static void AssertTreeDoesNotContain(
        string directory,
        IReadOnlyList<string> texts,
        string message)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal) ||
                normalized.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            AssertDoesNotContainAny(path, texts, message);
        }
    }
}
