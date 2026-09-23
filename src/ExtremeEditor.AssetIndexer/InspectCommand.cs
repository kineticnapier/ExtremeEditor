using System.Globalization;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ExtremeEditor.AssetIndexer;

internal static class InspectCommand
{
    private const int MaxDepth = 20;
    private const int MaxArrayItems = 64;

    public static bool HasInspectOption(string[] args)
        => args.Any(static arg => string.Equals(arg, "--inspect", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        try
        {
            InspectOptions options = InspectOptions.Parse(args);
            string gameRoot = ResolveGameRoot(options.GameRoot);
            string dataDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
            (string assetPath, long pathId) = ResolveTarget(gameRoot, dataDirectory, options.Target);

            Console.WriteLine($"[inspect] game={gameRoot}");
            Console.WriteLine($"[inspect] asset={Path.GetRelativePath(gameRoot, assetPath)} pathId={pathId}");
            if (options.ClassDataPath is not null)
                Console.WriteLine($"[inspect] classdata={Path.GetFullPath(options.ClassDataPath)}");

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

                AssetFileInfo? info = instance.file.GetAssetInfo(pathId);
                if (info is null)
                    throw new InvalidOperationException($"pathId {pathId} was not found in {assetPath}.");

                AssetTypeValueField root = manager.GetBaseField(instance, info);
                string? name = TryGetName(root);
                Console.WriteLine(
                    $"[inspect] type={(AssetClassID)info.TypeId} typeId={info.TypeId} bytes={info.ByteSize} name={name ?? "<unnamed>"}");
                Console.WriteLine($"[inspect] unity={instance.file.Metadata.UnityVersion} typeTree={instance.file.Metadata.TypeTreeEnabled}");
                Console.WriteLine();
                DumpField(root, 0);
                return 0;
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"inspect: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"inspect fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void DumpField(AssetTypeValueField field, int depth)
    {
        string indent = new(' ', depth * 2);
        Console.WriteLine($"{indent}{field.FieldName}: {field.TypeName}{DescribeValue(field)}");

        if (depth >= MaxDepth)
        {
            if (field.Children.Count > 0)
                Console.WriteLine($"{indent}  ... depth limit ({MaxDepth})");
            return;
        }

        int childCount = field.Children.Count;
        int limit = field.TemplateField.IsArray
            ? Math.Min(childCount, MaxArrayItems)
            : childCount;

        for (int i = 0; i < limit; i++)
            DumpField(field.Children[i], depth + 1);

        if (limit < childCount)
            Console.WriteLine($"{indent}  ... {childCount - limit} more array items omitted");
    }

    private static string DescribeValue(AssetTypeValueField field)
    {
        AssetValueType valueType = field.TemplateField.ValueType;

        if (valueType == AssetValueType.ByteArray)
        {
            byte[] data = field.AsByteArray;
            return $" = <{data.Length} bytes>";
        }

        if (field.TemplateField.IsArray || valueType == AssetValueType.Array)
            return $" [count={field.Children.Count}]";

        if (field.Children.Count > 0 || valueType == AssetValueType.None)
            return string.Empty;

        return valueType switch
        {
            AssetValueType.Bool => $" = {field.AsBool}",
            AssetValueType.Int8 => $" = {field.AsSByte}",
            AssetValueType.UInt8 => $" = {field.AsByte}",
            AssetValueType.Int16 => $" = {field.AsShort}",
            AssetValueType.UInt16 => $" = {field.AsUShort}",
            AssetValueType.Int32 => $" = {field.AsInt}",
            AssetValueType.UInt32 => $" = {field.AsUInt}",
            AssetValueType.Int64 => $" = {field.AsLong}",
            AssetValueType.UInt64 => $" = {field.AsULong}",
            AssetValueType.Float => $" = {field.AsFloat.ToString("R", CultureInfo.InvariantCulture)}",
            AssetValueType.Double => $" = {field.AsDouble.ToString("R", CultureInfo.InvariantCulture)}",
            AssetValueType.String => $" = {JsonSerializer.Serialize(Truncate(field.AsString, 240))}",
            AssetValueType.ManagedReferencesRegistry => " = <managed references>",
            _ => $" = <{valueType}>"
        };
    }

    private static string? TryGetName(AssetTypeValueField root)
    {
        AssetTypeValueField name = root["m_Name"];
        return name.IsDummy ? null : name.AsString;
    }

    private static (string AssetPath, long PathId) ResolveTarget(
        string gameRoot,
        string dataDirectory,
        string target)
    {
        int separator = target.LastIndexOf(':');
        if (separator <= 0 || separator == target.Length - 1)
            throw new ArgumentException("--inspect must be <asset-file>:<pathId>, for example resources.assets:3486.");

        string assetPart = target[..separator].Trim('"');
        string pathIdPart = target[(separator + 1)..];
        if (!long.TryParse(pathIdPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pathId))
            throw new ArgumentException($"Invalid inspect pathId: {pathIdPart}");

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
            throw new FileNotFoundException("Inspect asset file not found", assetPath);

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

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static void PrintUsage()
    {
        Console.WriteLine("Inspect usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- --adofai <dir> --inspect resources.assets:3486");
    }

    private sealed record InspectOptions(string? GameRoot, string? ClassDataPath, string Target)
    {
        public static InspectOptions Parse(string[] args)
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
                    case "--inspect":
                        target = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown inspect option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            if (target is null)
                throw new ArgumentException("Missing --inspect <asset-file>:<pathId>.");

            return new InspectOptions(gameRoot, classData, target);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
