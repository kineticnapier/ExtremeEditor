using System.Diagnostics;
using System.Text.Json;

namespace ExtremeEditor.Wpf;

public sealed record AssetCacheStatus(
    bool IsReady,
    string CacheRoot,
    string? GameVersion,
    string? SourceFingerprint,
    string? Error);

public sealed record AssetSetupRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    AssetCacheStatus CacheStatus);

public static class AssetSetupService
{
    public const int SupportedFormatVersion = 1;

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor",
        "AssetCache");

    public static string DefaultExtractorPath => Path.Combine(
        AppContext.BaseDirectory,
        "ExtremeEditor.AssetExtractor.exe");

    public static AssetCacheStatus InspectCache(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        string root = Path.GetFullPath(cacheRoot.Trim('"'));

        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifestPath))
                return NotReady(root, "manifest.json is missing.");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            JsonElement manifest = document.RootElement;

            if (!TryGetInt32(manifest, "formatVersion", out int formatVersion) ||
                formatVersion != SupportedFormatVersion)
            {
                return NotReady(root, $"Unsupported asset-cache formatVersion: {formatVersion}.");
            }

            string? gameVersion = GetRequiredString(manifest, "gameVersion");
            if (string.IsNullOrWhiteSpace(gameVersion))
                return NotReady(root, "manifest.json gameVersion is missing or empty.");

            string? sourceFingerprint = GetRequiredString(manifest, "sourceFingerprint");
            if (string.IsNullOrWhiteSpace(sourceFingerprint))
                return NotReady(root, "manifest.json sourceFingerprint is missing or empty.");

            string floorDirectory = Path.Combine(root, "floor-mesh");
            string floorIconDirectory = Path.Combine(root, "icons", "floors");
            string outlineDirectory = Path.Combine(root, "icons", "outlines");
            string eventDirectory = Path.Combine(root, "icons", "events");
            string categoryDirectory = Path.Combine(root, "icons", "categories");
            string hitSoundDirectory = Path.Combine(root, "hitsounds");

            foreach (string directory in new[]
                     {
                         floorDirectory,
                         floorIconDirectory,
                         outlineDirectory,
                         eventDirectory,
                         categoryDirectory,
                         hitSoundDirectory
                     })
            {
                if (!Directory.Exists(directory))
                    return NotReady(root, $"Asset-cache directory is missing: {directory}");
            }

            foreach (string fileName in new[] { "tile.png", "perlin.png", "ramp.png", "glow.png" })
            {
                string path = Path.Combine(floorDirectory, fileName);
                if (!File.Exists(path))
                    return NotReady(root, $"Floor texture is missing: {path}");
            }

            if (!ValidateCount(manifest, "floorIcons", floorIconDirectory, "*.png", out string? countError) ||
                !ValidateCount(manifest, "outlineIcons", outlineDirectory, "*.png", out countError) ||
                !ValidateCount(manifest, "eventIcons", eventDirectory, "*.png", out countError) ||
                !ValidateCount(manifest, "categoryIcons", categoryDirectory, "*.png", out countError) ||
                !ValidateCount(manifest, "hitSounds", hitSoundDirectory, "*.wav", out countError))
            {
                return NotReady(root, countError ?? "Asset-cache count validation failed.");
            }

            return new AssetCacheStatus(
                true,
                root,
                gameVersion,
                sourceFingerprint,
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return NotReady(root, $"Asset cache is invalid: {ex.Message}");
        }
    }

    public static ProcessStartInfo CreateExtractorStartInfo(
        string extractorPath,
        string cacheRoot,
        string adofaiRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(adofaiRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(extractorPath.Trim('"')),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--adofai");
        startInfo.ArgumentList.Add(Path.GetFullPath(adofaiRoot.Trim('"')));
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(Path.GetFullPath(cacheRoot.Trim('"')));
        return startInfo;
    }

    public static async Task<AssetSetupRunResult> RunExtractorAsync(
        string extractorPath,
        string cacheRoot,
        string adofaiRoot,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = CreateExtractorStartInfo(extractorPath, cacheRoot, adofaiRoot);
        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start ExtremeEditor.AssetExtractor.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        AssetCacheStatus status = InspectCache(cacheRoot);

        return new AssetSetupRunResult(process.ExitCode, stdout, stderr, status);
    }

    private static AssetCacheStatus NotReady(string root, string error)
        => new(false, root, null, null, error);

    private static bool ValidateCount(
        JsonElement manifest,
        string propertyName,
        string directory,
        string searchPattern,
        out string? error)
    {
        error = null;
        if (!TryGetInt32(manifest, propertyName, out int expected) || expected < 0)
        {
            error = $"manifest.json {propertyName} is missing or invalid.";
            return false;
        }

        int actual = Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).Count();
        if (actual != expected)
        {
            error = $"manifest.json {propertyName} mismatch: manifest={expected}, files={actual}.";
            return false;
        }

        return true;
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static string? GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}
