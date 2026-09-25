using System.IO;
using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class AdoFaiInstallationLocatorRegression
{
    public static void Run()
    {
        Assembly wpfAssembly = typeof(MainWindow).Assembly;
        Type locatorType = wpfAssembly.GetType("ExtremeEditor.Wpf.AdoFaiInstallationLocator")
            ?? throw new InvalidOperationException("AdoFaiInstallationLocator does not exist yet.");

        MethodInfo isGameRootMethod = locatorType.GetMethod(
            "IsGameRoot",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("AdoFaiInstallationLocator.IsGameRoot(string) is missing.");

        MethodInfo findMethod = locatorType.GetMethod(
            "FindInstalledGame",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string[])],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "AdoFaiInstallationLocator.FindInstalledGame(string? savedPath, string[] steamRoots) is missing.");

        string root = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-AdoFaiLocator-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            string savedInstall = Path.Combine(root, "Saved Install");
            CreateFakeGameRoot(savedInstall);

            if (isGameRootMethod.Invoke(null, [savedInstall]) is not true)
                throw new InvalidOperationException("A valid manually selected ADOFAI path must be accepted.");

            string? savedResult = findMethod.Invoke(null, [savedInstall, Array.Empty<string>()]) as string;
            AssertSamePath(savedResult, savedInstall, "Saved valid ADOFAI path must take priority.");

            string steamRoot = Path.Combine(root, "Steam Root");
            string customLibrary = Path.Combine(root, "Very Strange", "SteamLibrary");
            string customInstall = Path.Combine(
                customLibrary,
                "steamapps",
                "common",
                "Oddly Named Install Folder");
            CreateFakeGameRoot(customInstall);

            string steamApps = Path.Combine(steamRoot, "steamapps");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(
                Path.Combine(steamApps, "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n" +
                "  \"0\" { \"path\" \"" + EscapeVdfPath(steamRoot) + "\" }\n" +
                "  \"1\" { \"path\" \"" + EscapeVdfPath(customLibrary) + "\" }\n" +
                "}\n");

            string customSteamApps = Path.Combine(customLibrary, "steamapps");
            Directory.CreateDirectory(customSteamApps);
            File.WriteAllText(
                Path.Combine(customSteamApps, "appmanifest_977950.acf"),
                "\"AppState\"\n{\n" +
                "  \"appid\" \"977950\"\n" +
                "  \"installdir\" \"Oddly Named Install Folder\"\n" +
                "}\n");

            string? discovered = findMethod.Invoke(null, [null, new[] { steamRoot }]) as string;
            AssertSamePath(
                discovered,
                customInstall,
                "ADOFAI must be discovered from libraryfolders.vdf + appmanifest_977950.acf even in a custom Steam library.");

            string? missing = findMethod.Invoke(null, [Path.Combine(root, "Missing"), Array.Empty<string>()]) as string;
            if (missing is not null)
                throw new InvalidOperationException("Failed automatic discovery must return null so WPF can fall back to Browse.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private static void CreateFakeGameRoot(string gameRoot)
    {
        string data = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "resources.assets"), [1]);
        File.WriteAllBytes(Path.Combine(data, "resources.assets.resS"), [1]);
    }

    private static string EscapeVdfPath(string path)
        => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static void AssertSamePath(string? actual, string expected, string message)
    {
        if (actual is null ||
            !string.Equals(
                Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{message} expected={expected}, actual={actual ?? "<null>"}");
        }
    }
}
