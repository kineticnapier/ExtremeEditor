using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExtremeEditor.AssetExtractor;

public static class Program
{
    private const int ManifestFormatVersion = 1;

    public static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            string gameRoot = ResolveGameRoot(options.GameRoot);
            string cacheRoot = options.OutputDirectory is not null
                ? Path.GetFullPath(options.OutputDirectory.Trim('"'))
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExtremeEditor",
                    "AssetCache");

            string floorDirectory = Path.Combine(cacheRoot, "floor-mesh");
            string iconDirectory = Path.Combine(cacheRoot, "icons");
            string hitSoundDirectory = Path.Combine(cacheRoot, "hitsounds");
            string manifestPath = Path.Combine(cacheRoot, "manifest.json");

            Directory.CreateDirectory(cacheRoot);
            RecreateDirectory(floorDirectory);
            RecreateDirectory(iconDirectory);
            RecreateDirectory(hitSoundDirectory);
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);

            Console.WriteLine($"[extract] game={gameRoot}");
            Console.WriteLine($"[extract] cache={cacheRoot}");

            FloorTextureExtractionResult floorResult =
                AdoFaiFloorTextureExtractor.Extract(gameRoot, floorDirectory);
            IconExtractionResult iconResult =
                AdoFaiIconExtractor.Extract(gameRoot, iconDirectory);
            HitSoundExtractionResult hitSoundResult =
                AdoFaiHitSoundExtractor.Extract(gameRoot, hitSoundDirectory);

            string categoryDirectory = Path.Combine(iconDirectory, "categories");
            int categoryIconCount = Directory.Exists(categoryDirectory)
                ? Directory.EnumerateFiles(categoryDirectory, "*.png", SearchOption.TopDirectoryOnly).Count()
                : 0;

            string gameVersion = ReadGameVersion(gameRoot);
            string sourceFingerprint = ComputeSourceFingerprint(gameRoot);

            var manifest = new AssetCacheManifest(
                FormatVersion: ManifestFormatVersion,
                GameVersion: gameVersion,
                SourceFingerprint: sourceFingerprint,
                FloorIcons: iconResult.FloorIconCount,
                OutlineIcons: iconResult.OutlineIconCount,
                EventIcons: iconResult.EventIconCount,
                CategoryIcons: categoryIconCount,
                HitSounds: hitSoundResult.HitSoundCount);

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
            File.WriteAllBytes(manifestPath, manifestBytes);

            Console.WriteLine($"[extract] floor-mesh={floorResult.OutputDirectory}");
            Console.WriteLine($"[extract] icons={iconResult.OutputDirectory}");
            Console.WriteLine($"[extract] hitsounds={hitSoundResult.OutputDirectory}");
            Console.WriteLine($"[extract] manifest={manifestPath}");
            Console.WriteLine(
                $"[extract] complete floors={iconResult.FloorIconCount} outlines={iconResult.OutlineIconCount} " +
                $"events={iconResult.EventIconCount} categories={categoryIconCount} hitsounds={hitSoundResult.HitSoundCount} " +
                $"gameVersion={gameVersion} fingerprint={sourceFingerprint}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"extract fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static string ResolveGameRoot(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ValidateGameRoot(explicitPath);

        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "A Dance of Fire and Ice"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam", "steamapps", "common", "A Dance of Fire and Ice")
        ];

        foreach (string candidate in candidates)
        {
            if (IsGameRoot(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException(
            "ADOFAI installation was not found automatically. Pass --adofai <game directory>.");
    }

    private static string ValidateGameRoot(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        if (!IsGameRoot(fullPath))
            throw new DirectoryNotFoundException($"Not an ADOFAI installation directory: {fullPath}");
        return fullPath;
    }

    private static bool IsGameRoot(string root)
    {
        string data = Path.Combine(root, "A Dance of Fire and Ice_Data");
        return File.Exists(Path.Combine(data, "resources.assets")) &&
               File.Exists(Path.Combine(data, "resources.assets.resS")) &&
               File.Exists(Path.Combine(data, "Managed", "Assembly-CSharp.dll"));
    }

    private static string ReadGameVersion(string gameRoot)
    {
        string executablePath = Path.Combine(gameRoot, "A Dance of Fire and Ice.exe");
        if (File.Exists(executablePath))
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
            string? version = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            if (version is not null)
                return version;
        }

        string assemblyPath = Path.Combine(
            gameRoot,
            "A Dance of Fire and Ice_Data",
            "Managed",
            "Assembly-CSharp.dll");
        if (File.Exists(assemblyPath))
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(assemblyPath);
            string? version = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            if (version is not null)
                return version;
        }

        return "unknown-" + File.GetLastWriteTimeUtc(
            Path.Combine(gameRoot, "A Dance of Fire and Ice_Data", "resources.assets"))
            .ToString("yyyyMMddHHmmss");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string ComputeSourceFingerprint(string gameRoot)
    {
        string data = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
        string[] paths =
        [
            Path.Combine(data, "resources.assets"),
            Path.Combine(data, "resources.assets.resS"),
            Path.Combine(data, "globalgamemanagers"),
            Path.Combine(data, "Managed", "Assembly-CSharp.dll")
        ];

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths)
        {
            if (!File.Exists(path))
                continue;

            FileInfo info = new(path);
            AppendUtf8(hash, Path.GetFileName(path));
            AppendUtf8(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendUtf8(hash, info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Assembly-CSharp.dll is relatively small and changes whenever the managed
            // game API changes, so include its contents in addition to cheap metadata for
            // the much larger Unity asset/resource files.
            if (string.Equals(info.Name, "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
            {
                using FileStream stream = File.OpenRead(path);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ExtremeEditor.AssetExtractor");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ExtremeEditor.AssetExtractor [--adofai <dir>] [--output <dir>]");
        Console.WriteLine("  ExtremeEditor.AssetExtractor <adofai-root> [output-directory]");
        Console.WriteLine();
        Console.WriteLine("With no arguments, common Steam install paths are auto-detected and output is written to:");
        Console.WriteLine("  %LocalAppData%\\ExtremeEditor\\AssetCache");
    }

    private sealed record AssetCacheManifest(
        int FormatVersion,
        string GameVersion,
        string SourceFingerprint,
        int FloorIcons,
        int OutlineIcons,
        int EventIcons,
        int CategoryIcons,
        int HitSounds);

    private sealed record Options(
        string? GameRoot,
        string? OutputDirectory,
        bool ShowHelp)
    {
        public static Options Parse(string[] args)
        {
            string? gameRoot = null;
            string? outputDirectory = null;
            bool showHelp = false;
            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "--adofai":
                        gameRoot = RequireValue(args, ref i, arg);
                        break;
                    case "--output":
                        outputDirectory = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown option: {arg}");
                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count > 2)
                throw new ArgumentException("Too many positional arguments.");
            if (positional.Count >= 1)
            {
                if (gameRoot is not null)
                    throw new ArgumentException("ADOFAI root was specified both positionally and with --adofai.");
                gameRoot = positional[0];
            }
            if (positional.Count >= 2)
            {
                if (outputDirectory is not null)
                    throw new ArgumentException("Output directory was specified both positionally and with --output.");
                outputDirectory = positional[1];
            }

            return new Options(gameRoot, outputDirectory, showHelp);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
