using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class WinFormsHostRemovalRegression
{
    private static readonly string[] ProductionConfigurationExtensions =
    [
        ".csproj",
        ".sln",
        ".slnx",
        ".props",
        ".targets",
        ".ps1",
        ".yml",
        ".yaml"
    ];

    public static void Run()
    {
        string repositoryRoot = FindRepositoryRoot();
        string legacyProjectDirectory = Path.Combine(repositoryRoot, "src", "ExtremeEditor.App");
        string legacyLauncher = Path.Combine(repositoryRoot, "run-winforms.ps1");
        string productionLauncher = Path.Combine(repositoryRoot, "run.ps1");

        if (Directory.Exists(legacyProjectDirectory))
            throw new InvalidOperationException("The legacy src/ExtremeEditor.App project still exists.");

        if (File.Exists(legacyLauncher))
            throw new InvalidOperationException("The legacy run-winforms.ps1 launcher still exists.");

        if (!File.Exists(productionLauncher))
            throw new InvalidOperationException("The production run.ps1 launcher is missing.");

        string launcherText = File.ReadAllText(productionLauncher);
        if (!launcherText.Contains("ExtremeEditor.Wpf", StringComparison.Ordinal))
            throw new InvalidOperationException("run.ps1 must launch ExtremeEditor.Wpf.");
        if (launcherText.Contains("ExtremeEditor.App", StringComparison.Ordinal)
            || launcherText.Contains("run-winforms", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("run.ps1 still references the legacy WinForms host.");
        }

        string[] staleProductionReferences = Directory
            .EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => IsProductionConfigurationFile(repositoryRoot, path))
            .Where(path => File.ReadAllText(path).Contains("ExtremeEditor.App", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (staleProductionReferences.Length > 0)
        {
            throw new InvalidOperationException(
                "Production build/startup configuration still references ExtremeEditor.App: "
                + string.Join(", ", staleProductionReferences));
        }
    }

    private static bool IsProductionConfigurationFile(string repositoryRoot, string path)
    {
        string relativePath = Path.GetRelativePath(repositoryRoot, path);
        if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ".git"))
        {
            return false;
        }

        return ProductionConfigurationExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "ExtremeEditor.Wpf.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the ExtremeEditor repository root.");
    }
}
