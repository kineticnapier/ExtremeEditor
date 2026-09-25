namespace ExtremeEditor.Wpf;

public static class AdoFaiInstallationSettings
{
    private const string FileName = "adofai-installation.txt";

    public static string DefaultSettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor");

    public static string? LoadSavedPath(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        string path = Path.Combine(Path.GetFullPath(settingsDirectory), FileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SavePath(string settingsDirectory, string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        string directory = Path.GetFullPath(settingsDirectory);
        string normalizedGameRoot = Path.GetFullPath(gameRoot.Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), normalizedGameRoot);
    }
}
