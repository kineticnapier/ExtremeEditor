using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class AssetExtractorPackagingRegression
{
    public static void Run()
    {
        string wpfOutput = FindWpfOutputDirectory();
        string extractorDirectory = Path.Combine(wpfOutput, "AssetExtractor");
        string extractorPath = Path.Combine(extractorDirectory, "ExtremeEditor.AssetExtractor.exe");
        string extractorNvPath = Path.Combine(extractorDirectory, "NVorbis.dll");

        if (!File.Exists(extractorPath))
        {
            throw new InvalidOperationException(
                "Packaged AssetExtractor is not isolated from WPF runtime dependencies. " +
                $"Expected '{extractorPath}'.");
        }

        if (!File.Exists(extractorNvPath))
        {
            throw new InvalidOperationException(
                "Packaged AssetExtractor is not isolated from WPF runtime dependencies: " +
                $"missing '{extractorNvPath}'.");
        }

        string depsPath = Path.Combine(extractorDirectory, "ExtremeEditor.AssetExtractor.deps.json");
        Version requiredNvVersion = ReadRequiredAssemblyVersion(depsPath, "NVorbis.dll");
        Version packagedNvVersion = AssemblyName.GetAssemblyName(extractorNvPath).Version
            ?? throw new InvalidOperationException("Packaged AssetExtractor NVorbis has no AssemblyVersion.");
        if (packagedNvVersion != requiredNvVersion)
        {
            throw new InvalidOperationException(
                $"Packaged AssetExtractor NVorbis mismatch: required {requiredNvVersion}, " +
                $"found {packagedNvVersion}.");
        }

        string wpfNvPath = Path.Combine(wpfOutput, "NVorbis.dll");
        if (File.Exists(wpfNvPath) &&
            string.Equals(
                Path.GetFullPath(wpfNvPath),
                Path.GetFullPath(extractorNvPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Packaged AssetExtractor must not share the WPF root NVorbis file.");
        }

        if (!string.Equals(
                Path.GetFullPath(AssetSetupService.DefaultExtractorPath),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "AssetExtractor",
                    "ExtremeEditor.AssetExtractor.exe")),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AssetSetupService.DefaultExtractorPath must target the isolated AssetExtractor executable.");
        }

        string? gameRoot = FindAdoFaiInstallation();
        if (gameRoot is null)
        {
            Console.WriteLine("SKIP: packaged AssetExtractor integration (ADOFAI installation not found).");
            return;
        }

        RunPackagedExtractorIntegration(extractorPath, gameRoot);
    }

    private static string FindWpfOutputDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? configuration = null;
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "Debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directory.Name, "Release", StringComparison.OrdinalIgnoreCase))
            {
                configuration ??= directory.Name;
            }

            if (File.Exists(Path.Combine(directory.FullName, "ExtremeEditor.Wpf.Tests.csproj")))
            {
                DirectoryInfo src = directory.Parent
                    ?? throw new InvalidOperationException("Unable to locate the repository src directory.");
                string candidate = Path.Combine(
                    src.FullName,
                    "ExtremeEditor.Wpf",
                    "bin",
                    configuration ?? "Release",
                    "net8.0-windows");
                if (Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);

                throw new DirectoryNotFoundException($"WPF output directory was not found: {candidate}");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Unable to locate ExtremeEditor.Wpf.Tests.csproj from {AppContext.BaseDirectory}.");
    }

    private static Version ReadRequiredAssemblyVersion(string depsPath, string assemblyFileName)
    {
        if (!File.Exists(depsPath))
            throw new FileNotFoundException("Packaged AssetExtractor deps.json is missing.", depsPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        JsonElement root = document.RootElement;
        string targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new InvalidDataException("AssetExtractor deps.json runtimeTarget.name is missing.");
        JsonElement target = root.GetProperty("targets").GetProperty(targetName);

        foreach (JsonProperty library in target.EnumerateObject())
        {
            if (!library.Name.StartsWith("NVorbis/", StringComparison.OrdinalIgnoreCase) ||
                !library.Value.TryGetProperty("runtime", out JsonElement runtime))
            {
                continue;
            }

            foreach (JsonProperty runtimeAssembly in runtime.EnumerateObject())
            {
                if (!string.Equals(
                        Path.GetFileName(runtimeAssembly.Name.Replace('/', Path.DirectorySeparatorChar)),
                        assemblyFileName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !runtimeAssembly.Value.TryGetProperty(
                        "assemblyVersion",
                        out JsonElement versionElement))
                {
                    continue;
                }

                string? versionText = versionElement.GetString();
                if (Version.TryParse(versionText, out Version? version))
                    return version;
            }
        }

        throw new InvalidDataException(
            $"AssetExtractor deps.json does not declare the required {assemblyFileName} AssemblyVersion.");
    }

    private static string? FindAdoFaiInstallation()
    {
        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "A Dance of Fire and Ice"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam", "steamapps", "common", "A Dance of Fire and Ice")
        ];

        return candidates.FirstOrDefault(candidate =>
            File.Exists(Path.Combine(candidate, "A Dance of Fire and Ice_Data", "resources.assets")) &&
            File.Exists(Path.Combine(candidate, "A Dance of Fire and Ice_Data", "Managed", "Assembly-CSharp.dll")));
    }

    private static void RunPackagedExtractorIntegration(string extractorPath, string gameRoot)
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-PackagedAssetExtractor-{Guid.NewGuid():N}");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = extractorPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--adofai");
            startInfo.ArgumentList.Add(gameRoot);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(output);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start packaged AssetExtractor.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Packaged AssetExtractor exited with {process.ExitCode}: {stderr}");
            }

            if (!File.Exists(Path.Combine(output, "hitsounds", "sndSizzle.wav")))
                throw new InvalidOperationException("Packaged AssetExtractor did not produce sndSizzle.wav.");

            string manifestPath = Path.Combine(output, "manifest.json");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            int hitSoundCount = manifest.RootElement.GetProperty("hitSounds").GetInt32();
            if (hitSoundCount != 27)
            {
                throw new InvalidOperationException(
                    $"Packaged AssetExtractor manifest hitSounds: expected 27, actual {hitSoundCount}.");
            }

            string[] names = manifest.RootElement.GetProperty("hitSoundNames")
                .EnumerateArray()
                .Select(static element => element.GetString() ?? string.Empty)
                .ToArray();
            if (!names.Contains("Sizzle", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Packaged AssetExtractor manifest does not declare Sizzle.");

            if (!stdout.Contains("HitSound enum values=28", StringComparison.Ordinal) ||
                !stdout.Contains("hitsounds exported=27 missing=0 failed=0", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Packaged AssetExtractor did not extract the complete current HitSound set.");
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                try
                {
                    Directory.Delete(output, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}
