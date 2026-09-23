using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ExtremeEditor.AssetIndexer;

internal static class ComponentInspectCommand
{
    public static bool HasOption(string[] args)
        => args.Any(static arg => string.Equals(arg, "--inspect-components", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            string gameRoot = ResolveGameRoot(options.GameRoot);
            string dataDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
            (string assetPath, long gameObjectPathId) = ResolveTarget(gameRoot, dataDirectory, options.Target);

            Console.WriteLine($"[components] game={gameRoot}");
            Console.WriteLine($"[components] asset={Path.GetRelativePath(gameRoot, assetPath)} gameObjectPathId={gameObjectPathId}");
            if (options.ClassDataPath is not null)
                Console.WriteLine($"[components] classdata={Path.GetFullPath(options.ClassDataPath)}");

            var manager = new AssetsManager();
            try
            {
                bool hasClassData = false;
                if (options.ClassDataPath is not null)
                {
                    if (!File.Exists(options.ClassDataPath))
                        throw new FileNotFoundException("classdata.tpk not found", options.ClassDataPath);
                    manager.LoadClassPackage(options.ClassDataPath);
                    hasClassData = true;
                }

                AssetsFileInstance instance = manager.LoadAssetsFile(assetPath, false);
                if (hasClassData)
                    manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

                AssetFileInfo? gameObjectInfo = instance.file.GetAssetInfo(gameObjectPathId);
                if (gameObjectInfo is null)
                    throw new InvalidOperationException($"pathId {gameObjectPathId} was not found in {assetPath}.");
                if ((AssetClassID)gameObjectInfo.TypeId != AssetClassID.GameObject)
                    throw new InvalidOperationException($"pathId {gameObjectPathId} is {(AssetClassID)gameObjectInfo.TypeId}, not GameObject.");

                AssetTypeValueField gameObject = manager.GetBaseField(instance, gameObjectInfo);
                string name = ReadString(gameObject["m_Name"]) ?? "<unnamed>";
                AssetTypeValueField componentsArray = gameObject["m_Component"]["Array"];
                if (componentsArray.IsDummy)
                    throw new InvalidOperationException("GameObject has no m_Component array.");

                Console.WriteLine($"[components] name={name} count={componentsArray.Children.Count}");
                Console.WriteLine();

                for (int i = 0; i < componentsArray.Children.Count; i++)
                {
                    AssetTypeValueField componentPtr = componentsArray.Children[i]["component"];
                    long componentPathId = ReadPathId(componentPtr);
                    if (componentPathId == 0)
                    {
                        Console.WriteLine($"[component {i}] <null>");
                        continue;
                    }

                    AssetFileInfo? componentInfo = instance.file.GetAssetInfo(componentPathId);
                    if (componentInfo is null)
                    {
                        Console.WriteLine($"[component {i}] pathId={componentPathId} <missing>");
                        continue;
                    }

                    AssetClassID classId = (AssetClassID)componentInfo.TypeId;
                    AssetTypeValueField component = manager.GetBaseField(instance, componentInfo);
                    string? componentName = ReadString(component["m_Name"]);
                    Console.WriteLine(
                        $"[component {i}] pathId={componentPathId} type={classId} typeId={componentInfo.TypeId}" +
                        (string.IsNullOrEmpty(componentName) ? string.Empty : $" name={componentName}"));

                    PrintPointer(component, "m_Mesh", "mesh");
                    PrintPointer(component, "m_Sprite", "sprite");
                    PrintPointer(component, "m_Script", "script");
                    PrintPointers(component, "m_Materials", "material");
                }

                return 0;
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"components: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"components fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void PrintPointer(AssetTypeValueField root, string fieldName, string label)
    {
        AssetTypeValueField field = root[fieldName];
        if (field.IsDummy)
            return;

        if (!TryReadPointer(field, out int fileId, out long pathId))
            return;

        Console.WriteLine($"  {label}: fileId={fileId} pathId={pathId}");
    }

    private static void PrintPointers(AssetTypeValueField root, string fieldName, string label)
    {
        AssetTypeValueField field = root[fieldName];
        if (field.IsDummy)
            return;

        AssetTypeValueField array = field["Array"];
        if (array.IsDummy)
            return;

        for (int i = 0; i < array.Children.Count; i++)
        {
            if (TryReadPointer(array.Children[i], out int fileId, out long pathId))
                Console.WriteLine($"  {label}[{i}]: fileId={fileId} pathId={pathId}");
        }
    }

    private static bool TryReadPointer(AssetTypeValueField field, out int fileId, out long pathId)
    {
        fileId = 0;
        pathId = 0;

        AssetTypeValueField file = field["m_FileID"];
        AssetTypeValueField path = field["m_PathID"];
        if (file.IsDummy || path.IsDummy)
            return false;

        fileId = file.AsInt;
        pathId = path.AsLong;
        return true;
    }

    private static long ReadPathId(AssetTypeValueField pointer)
    {
        AssetTypeValueField path = pointer["m_PathID"];
        return path.IsDummy ? 0 : path.AsLong;
    }

    private static string? ReadString(AssetTypeValueField field)
        => field.IsDummy ? null : field.AsString;

    private static (string AssetPath, long PathId) ResolveTarget(
        string gameRoot,
        string dataDirectory,
        string target)
    {
        int separator = target.LastIndexOf(':');
        if (separator <= 0 || separator == target.Length - 1)
            throw new ArgumentException("--inspect-components must be <asset-file>:<gameObjectPathId>.");

        string assetPart = target[..separator].Trim('"');
        string pathIdPart = target[(separator + 1)..];
        if (!long.TryParse(pathIdPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pathId))
            throw new ArgumentException($"Invalid GameObject pathId: {pathIdPart}");

        string assetPath;
        if (Path.IsPathRooted(assetPart))
        {
            assetPath = Path.GetFullPath(assetPart);
        }
        else
        {
            string fromDataDirectory = Path.GetFullPath(Path.Combine(dataDirectory, assetPart));
            string fromGameRoot = Path.GetFullPath(Path.Combine(gameRoot, assetPart));
            assetPath = File.Exists(fromDataDirectory) ? fromDataDirectory : fromGameRoot;
        }

        if (!File.Exists(assetPath))
            throw new FileNotFoundException("Component inspect asset file not found", assetPath);

        return (assetPath, pathId);
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

        throw new DirectoryNotFoundException(
            "ADOFAI installation was not found automatically. Pass --adofai <game directory>.");
    }

    private static string ValidateGameRoot(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        if (!Directory.Exists(Path.Combine(fullPath, "A Dance of Fire and Ice_Data")))
            throw new DirectoryNotFoundException($"Not an ADOFAI installation directory: {fullPath}");
        return fullPath;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Component inspect usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- --adofai <dir> --inspect-components sharedassets4.assets:158");
    }

    private sealed record Options(string? GameRoot, string? ClassDataPath, string Target)
    {
        public static Options Parse(string[] args)
        {
            string? gameRoot = null;
            string? classData = null;
            string? target = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--adofai":
                        gameRoot = RequireValue(args, ref i, arg);
                        break;
                    case "--classdata":
                        classData = RequireValue(args, ref i, arg);
                        break;
                    case "--inspect-components":
                        target = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown component inspect option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            if (target is null)
                throw new ArgumentException("Missing --inspect-components <asset-file>:<gameObjectPathId>.");

            return new Options(gameRoot, classData, target);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
