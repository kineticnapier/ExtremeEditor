using System.Text.RegularExpressions;

namespace ExtremeEditor.Wpf;

public static partial class AdoFaiInstallationLocator
{
    private const string AppId = "977950";
    private const string NeoCosmosAppId = "1977570";
    private const string NeoCosmosDepot32 = "1977571";
    private const string NeoCosmosDepot64 = "1977572";
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

    public static bool HasInstalledNeoCosmos(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            return false;

        try
        {
            string root = Normalize(gameRoot);
            var installDirectory = new DirectoryInfo(root);
            DirectoryInfo? commonDirectory = installDirectory.Parent;
            DirectoryInfo? steamAppsDirectory = commonDirectory?.Parent;

            if (commonDirectory is null ||
                steamAppsDirectory is null ||
                !string.Equals(commonDirectory.Name, "common", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(steamAppsDirectory.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string manifestPath = Path.Combine(steamAppsDirectory.FullName, $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifestPath))
                return false;

            string text = File.ReadAllText(manifestPath);

            bool disabled = DisabledNeoCosmosRegex().IsMatch(text) ||
                            ManifestSectionContainsAnyKey(text, "DisabledDLC", NeoCosmosAppId);
            if (disabled)
                return false;

            return ManifestSectionContainsAnyKey(text, "InstalledDepots", NeoCosmosDepot32, NeoCosmosDepot64) ||
                   ManifestSectionContainsAnyKey(text, "MountedDepots", NeoCosmosDepot32, NeoCosmosDepot64);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
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

    private static bool ManifestSectionContainsAnyKey(string text, string sectionName, params string[] keys)
    {
        Match section = Regex.Match(
            text,
            $"\\\"{Regex.Escape(sectionName)}\\\"\\s*\\{{",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!section.Success)
            return false;

        int openingBrace = text.IndexOf('{', section.Index + section.Length - 1);
        if (openingBrace < 0)
            return false;

        int closingBrace = FindMatchingBrace(text, openingBrace);
        if (closingBrace <= openingBrace)
            return false;

        string body = text[(openingBrace + 1)..closingBrace];
        foreach (string key in keys)
        {
            if (Regex.IsMatch(
                    body,
                    $"\\\"{Regex.Escape(key)}\\\"\\s*(?:\\{{|\\\")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindMatchingBrace(string text, int openingBrace)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = openingBrace; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}' && --depth == 0)
                return i;
        }

        return -1;
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

    [GeneratedRegex("\\\"DisabledDLC\\\"\\s*\\\"[^\\\"]*\\b1977570\\b[^\\\"]*\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex DisabledNeoCosmosRegex();
}
