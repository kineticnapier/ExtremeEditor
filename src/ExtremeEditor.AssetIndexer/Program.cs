using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ExtremeEditor.AssetIndexer;

internal static class Program
{
    private static readonly AssetClassID[] TargetTypes =
    [
        AssetClassID.Texture2D,
        AssetClassID.Sprite,
        AssetClassID.AudioClip,
        AssetClassID.Mesh,
        AssetClassID.Material,
        AssetClassID.GameObject,
        AssetClassID.MonoBehaviour,
        AssetClassID.MonoScript
    ];

    private static readonly string[] DefaultNeedles =
    [
        "FloorMeshDefault",
        "meshFloor",
        "spriteFloor",
        "sndKick",
        "sndHat",
        "sndClap",
        "LevelSettings",
        "SetHitsound",
        "SetFloorIcon",
        "tile_rabbit",
        "tile_snail",
        "tile_hold",
        "swirl_",
        "RDConstants"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
            string dataDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
            if (!Directory.Exists(dataDirectory))
                throw new DirectoryNotFoundException($"ADOFAI data directory not found: {dataDirectory}");

            string outputPath = options.OutputPath ?? BuildDefaultOutputPath();
            string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            List<string> serializedFiles = DiscoverSerializedFiles(dataDirectory, options.IncludeLevels);
            List<string> bundleFiles = options.IncludeBundles
                ? DiscoverBundleFiles(gameRoot)
                : [];

            Console.WriteLine($"[index] game={gameRoot}");
            Console.WriteLine($"[index] serializedFiles={serializedFiles.Count} bundles={bundleFiles.Count}");
            Console.WriteLine($"[index] output={Path.GetFullPath(outputPath)}");
            if (options.ClassDataPath is null)
                Console.WriteLine("[index] classdata=<none>; embedded type trees will be used when available");
            else
                Console.WriteLine($"[index] classdata={Path.GetFullPath(options.ClassDataPath)}");

            using var output = new StreamWriter(outputPath, false, new UTF8Encoding(false), 1 << 16);
            var manager = new AssetsManager();
            bool hasClassData = false;
            if (options.ClassDataPath is not null)
            {
                if (!File.Exists(options.ClassDataPath))
                    throw new FileNotFoundException("classdata.tpk not found", options.ClassDataPath);
                manager.LoadClassPackage(options.ClassDataPath);
                hasClassData = true;
            }

            var stats = new ScanStats();
            string[] needles = DefaultNeedles.Concat(options.Queries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (string path in serializedFiles)
            {
                string source = Path.GetRelativePath(gameRoot, path);
                Console.WriteLine($"[index] assets {source}");
                try
                {
                    AssetsFileInstance instance = manager.LoadAssetsFile(path, false);
                    PrepareClassDatabase(manager, instance, hasClassData, stats, source);
                    ScanAssetsFile(manager, instance, source, null, output, needles, stats);
                    manager.UnloadAssetsFile(instance);
                }
                catch (Exception ex)
                {
                    stats.FileErrors++;
                    Console.Error.WriteLine($"[index] ERROR {source}: {ex.GetType().Name}: {ex.Message}");
                    manager.UnloadAllAssetsFiles(false);
                }
            }

            foreach (string path in bundleFiles)
            {
                string source = Path.GetRelativePath(gameRoot, path);
                Console.WriteLine($"[index] bundle {source}");
                BundleFileInstance? bundle = null;
                try
                {
                    bundle = manager.LoadBundleFile(path, true);
                    int entryCount = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
                    for (int i = 0; i < entryCount; i++)
                    {
                        if (!bundle.file.IsAssetsFile(i))
                            continue;

                        AssetsFileInstance? instance = manager.LoadAssetsFileFromBundle(bundle, i, false);
                        if (instance is null)
                            continue;

                        PrepareClassDatabase(manager, instance, hasClassData, stats, $"{source}::{instance.name}");
                        ScanAssetsFile(manager, instance, source, instance.name, output, needles, stats);
                    }
                }
                catch (Exception ex)
                {
                    stats.FileErrors++;
                    Console.Error.WriteLine($"[index] ERROR {source}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (bundle is not null)
                        manager.UnloadBundleFile(bundle);
                }
            }

            output.Flush();
            manager.UnloadAll(true);

            Console.WriteLine($"[index] done entries={stats.Entries} named={stats.NamedEntries} matches={stats.Matches} nameErrors={stats.NameErrors} fileErrors={stats.FileErrors} classdataErrors={stats.ClassDatabaseErrors}");
            Console.WriteLine($"[index] jsonl={Path.GetFullPath(outputPath)}");
            if (!hasClassData && stats.NameErrors > 0)
                Console.WriteLine("[index] Some names could not be decoded. Re-run with --classdata <classdata.tpk> if the target files have stripped type trees.");

            return stats.FileErrors == 0 ? 0 : 1;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void PrepareClassDatabase(
        AssetsManager manager,
        AssetsFileInstance instance,
        bool hasClassData,
        ScanStats stats,
        string source)
    {
        if (!hasClassData)
            return;

        try
        {
            manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);
        }
        catch (Exception ex)
        {
            stats.ClassDatabaseErrors++;
            Console.Error.WriteLine($"[index] classdata warning {source}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ScanAssetsFile(
        AssetsManager manager,
        AssetsFileInstance instance,
        string source,
        string? bundleEntry,
        StreamWriter output,
        IReadOnlyList<string> needles,
        ScanStats stats)
    {
        string unityVersion = instance.file.Metadata.UnityVersion.ToString();
        bool typeTreeEnabled = instance.file.Metadata.TypeTreeEnabled;

        foreach (AssetClassID classId in TargetTypes)
        {
            foreach (AssetFileInfo info in instance.file.GetAssetsOfType(classId))
            {
                string? name = TryReadName(manager, instance, info, out string? readError);
                if (readError is not null)
                    stats.NameErrors++;
                if (!string.IsNullOrEmpty(name))
                    stats.NamedEntries++;

                var entry = new AssetIndexEntry(
                    Source: source,
                    BundleEntry: bundleEntry,
                    UnityVersion: unityVersion,
                    TypeTreeEnabled: typeTreeEnabled,
                    PathId: info.PathId,
                    Type: classId.ToString(),
                    TypeId: info.TypeId,
                    ByteSize: info.ByteSize,
                    Name: name,
                    ReadError: readError);

                output.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                stats.Entries++;

                if (!string.IsNullOrEmpty(name) && MatchesAny(name, needles))
                {
                    stats.Matches++;
                    string location = bundleEntry is null ? source : $"{source}::{bundleEntry}";
                    Console.WriteLine($"[match] {classId} pathId={info.PathId} name={name} source={location}");
                }
            }
        }
    }

    private static string? TryReadName(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetFileInfo info,
        out string? error)
    {
        error = null;
        try
        {
            AssetTypeValueField root = manager.GetBaseField(instance, info);
            AssetTypeValueField nameField = root["m_Name"];
            if (nameField.IsDummy)
                return null;
            return nameField.AsString;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + Truncate(ex.Message, 180);
            return null;
        }
    }

    private static bool MatchesAny(string value, IReadOnlyList<string> needles)
    {
        for (int i = 0; i < needles.Count; i++)
        {
            if (value.Contains(needles[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static List<string> DiscoverSerializedFiles(string dataDirectory, bool includeLevels)
    {
        var paths = Directory.EnumerateFiles(dataDirectory, "*.assets", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (includeLevels)
        {
            paths.AddRange(Directory.EnumerateFiles(dataDirectory, "level*", SearchOption.TopDirectoryOnly)
                .Where(IsLevelFile)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
        }

        return paths;
    }

    private static List<string> DiscoverBundleFiles(string gameRoot)
    {
        string bundlesDirectory = Path.Combine(gameRoot, "Bundles");
        if (!Directory.Exists(bundlesDirectory))
            return [];

        return Directory.EnumerateFiles(bundlesDirectory, "*.bundle", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLevelFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("level", StringComparison.OrdinalIgnoreCase)
               && name.Length > 5
               && int.TryParse(name[5..], out _);
    }

    private static string ResolveGameRoot(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ValidateGameRoot(explicitPath);

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "A Dance of Fire and Ice"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "A Dance of Fire and Ice")
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "A Dance of Fire and Ice_Data")))
                return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException("ADOFAI installation was not found automatically. Pass --adofai <game directory>.");
    }

    private static string ValidateGameRoot(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        if (!Directory.Exists(Path.Combine(fullPath, "A Dance of Fire and Ice_Data")))
            throw new DirectoryNotFoundException($"Not an ADOFAI installation directory: {fullPath}");
        return fullPath;
    }

    private static string BuildDefaultOutputPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "AssetIndexer");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"adofai-assets-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static void PrintUsage()
    {
        Console.WriteLine("ExtremeEditor.AssetIndexer");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --adofai <dir>       ADOFAI install directory. Common Steam paths are auto-detected.");
        Console.WriteLine("  --output <file>      JSONL output path. Defaults to %TEMP%/ExtremeEditor/AssetIndexer/...");
        Console.WriteLine("  --classdata <file>   Optional AssetsTools.NET classdata.tpk for stripped type trees.");
        Console.WriteLine("  --query <text>       Also print names containing this text. Can be repeated.");
        Console.WriteLine("  --include-levels     Scan level0, level1, ... in addition to *.assets files.");
        Console.WriteLine("  --no-bundles         Skip the game's Bundles/*.bundle files.");
        Console.WriteLine("  -h, --help           Show this help.");
    }

    private sealed record AssetIndexEntry(
        string Source,
        string? BundleEntry,
        string UnityVersion,
        bool TypeTreeEnabled,
        long PathId,
        string Type,
        int TypeId,
        uint ByteSize,
        string? Name,
        string? ReadError);

    private sealed class ScanStats
    {
        public long Entries;
        public long NamedEntries;
        public long Matches;
        public long NameErrors;
        public int FileErrors;
        public int ClassDatabaseErrors;
    }

    private sealed record Options(
        string? GameRoot,
        string? OutputPath,
        string? ClassDataPath,
        bool IncludeLevels,
        bool IncludeBundles,
        bool ShowHelp,
        string[] Queries)
    {
        public static Options Parse(string[] args)
        {
            string? gameRoot = null;
            string? output = null;
            string? classData = null;
            bool includeLevels = false;
            bool includeBundles = true;
            bool showHelp = false;
            var queries = new List<string>();

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
                        output = RequireValue(args, ref i, arg);
                        break;
                    case "--classdata":
                        classData = RequireValue(args, ref i, arg);
                        break;
                    case "--query":
                        queries.Add(RequireValue(args, ref i, arg));
                        break;
                    case "--include-levels":
                        includeLevels = true;
                        break;
                    case "--no-bundles":
                        includeBundles = false;
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            return new Options(gameRoot, output, classData, includeLevels, includeBundles, showHelp, queries.ToArray());
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
