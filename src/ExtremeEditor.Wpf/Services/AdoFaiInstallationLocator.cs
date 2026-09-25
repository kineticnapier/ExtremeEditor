using System.Text.RegularExpressions;

namespace ExtremeEditor.Wpf;

public static partial class AdoFaiInstallationLocator
{
    private const string AppId = "977950";
    private const string DefaultInstallDirectory = "A Dance of Fire and Ice";

    public static bool IsGameRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string root = Path.GetFullPath(path.Trim('"'));
            string data = Path.Combine(root, "A Dance of Fire and Ice_Data");
            return File.Exists(Path.Combine(data, "resources.assets")) &&
                   File.Exists(Path.Combine(data, "resources.assets.resS"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string? FindInstalledGame(string? savedPath, string[] steamRoots)
    {
        if (!string.IsNullOrWhiteSpace(savedPath) && IsGameRoot(savedPath))
            return Normalize(savedPath);

        foreach (string steamRoot in steamRoots ?? Array.Empty<string>())
        {
            foreach (string libraryRoot in EnumerateLibraryRoots(steamRoot))
            {
                string? fromManifest = ResolveInstallFromManifest(libraryRoot);
                if (fromManifest is not null)
                    return fromManifest;

                string fallback = Path.Combine(
                    libraryRoot,
                    "steamapps",
                    "common",
                    DefaultInstallDirectory);
                if (IsGameRoot(fallback))
                    return Normalize(fallback);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLibraryRoots(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot))
            yield break;

        string normalizedRoot;
        try
        {
            normalizedRoot = Normalize(steamRoot);
        }
        catch
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(normalizedRoot))
            yield return normalizedRoot;

        string libraryFoldersPath = Path.Combine(normalizedRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
            yield break;

        string text;
        try
        {
            text = File.ReadAllText(libraryFoldersPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (Match match in VdfPathRegex().Matches(text))
        {
            string candidate = UnescapeVdfString(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string normalized;
            try
            {
                normalized = Normalize(candidate);
            }
            catch
            {
                continue;
            }

            if (seen.Add(normalized))
                yield return normalized;
        }
    }

    private static string? ResolveInstallFromManifest(string libraryRoot)
    {
        string manifestPath = Path.Combine(
            libraryRoot,
            "steamapps",
            $"appmanifest_{AppId}.acf");
        if (!File.Exists(manifestPath))
            return null;

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Match match = InstallDirRegex().Match(text);
        if (!match.Success)
            return null;

        string installDir = UnescapeVdfString(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(installDir))
            return null;

        string candidate = Path.Combine(
            libraryRoot,
            "steamapps",
            "common",
            installDir);
        return IsGameRoot(candidate) ? Normalize(candidate) : null;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path.Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string UnescapeVdfString(string value)
        => value.Replace("\\\\", "\\", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal);

    [GeneratedRegex("\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();
}
