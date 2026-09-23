using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace ExtremeEditor.AssetIndexer;

internal static class DecompileTypeCommand
{
    public static bool HasOption(string[] args)
        => args.Any(static arg => string.Equals(arg, "--decompile-type", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            string gameRoot = ResolveGameRoot(options.GameRoot);
            string managedDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data", "Managed");
            string assemblyPath = Path.IsPathRooted(options.Assembly)
                ? Path.GetFullPath(options.Assembly)
                : Path.Combine(managedDirectory, options.Assembly);

            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Managed assembly not found", assemblyPath);

            Console.WriteLine($"[decompile] game={gameRoot}");
            Console.WriteLine($"[decompile] assembly={Path.GetRelativePath(gameRoot, assemblyPath)}");
            Console.WriteLine($"[decompile] type={options.TypeName}");
            Console.WriteLine();

            var settings = new DecompilerSettings();
            var decompiler = new CSharpDecompiler(assemblyPath, settings);
            string source = decompiler.DecompileTypeAsString(new FullTypeName(options.TypeName));
            Console.Write(source);
            if (!source.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                Console.WriteLine();
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"decompile: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"decompile fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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
            if (Directory.Exists(Path.Combine(candidate, "A Dance of Fire and Ice_Data", "Managed")))
                return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException(
            "ADOFAI installation was not found automatically. Pass --adofai <game directory>.");
    }

    private static string ValidateGameRoot(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        if (!Directory.Exists(Path.Combine(fullPath, "A Dance of Fire and Ice_Data", "Managed")))
            throw new DirectoryNotFoundException($"Not an ADOFAI installation directory: {fullPath}");
        return fullPath;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Decompile usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- --adofai <dir> --decompile-type FloorMesh");
        Console.WriteLine("  optional: --assembly Assembly-CSharp.dll");
    }

    private sealed record Options(string? GameRoot, string Assembly, string TypeName)
    {
        public static Options Parse(string[] args)
        {
            string? gameRoot = null;
            string assembly = "Assembly-CSharp.dll";
            string? typeName = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--adofai":
                        gameRoot = RequireValue(args, ref i, arg);
                        break;
                    case "--assembly":
                        assembly = RequireValue(args, ref i, arg);
                        break;
                    case "--decompile-type":
                        typeName = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown decompile option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Missing --decompile-type <type-name>.");

            return new Options(gameRoot, assembly, typeName);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
