using System.Globalization;
using System.Text;

namespace ExtremeEditor.AssetIndexer;

internal static class PeekResourceCommand
{
    private const int MaxPeekLength = 4096;

    public static bool HasPeekOption(string[] args)
        => args.Any(static arg => string.Equals(arg, "--peek-resource", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        try
        {
            PeekOptions options = PeekOptions.Parse(args);
            string gameRoot = ResolveGameRoot(options.GameRoot);
            string dataDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data");
            (string resourcePath, long offset, int length) = ResolveTarget(gameRoot, dataDirectory, options.Target);

            using var stream = new FileStream(resourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset > stream.Length)
                throw new ArgumentOutOfRangeException(nameof(options.Target), $"Offset {offset} is past EOF ({stream.Length}).");

            int actualLength = (int)Math.Min(length, stream.Length - offset);
            byte[] buffer = new byte[actualLength];
            stream.Position = offset;
            stream.ReadExactly(buffer);

            Console.WriteLine($"[peek] game={gameRoot}");
            Console.WriteLine($"[peek] resource={Path.GetRelativePath(gameRoot, resourcePath)}");
            Console.WriteLine($"[peek] offset={offset} requested={length} read={actualLength} fileSize={stream.Length}");
            Console.WriteLine();
            DumpHex(buffer, offset);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"peek: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"peek fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void DumpHex(ReadOnlySpan<byte> data, long baseOffset)
    {
        const int bytesPerLine = 16;
        for (int i = 0; i < data.Length; i += bytesPerLine)
        {
            ReadOnlySpan<byte> line = data.Slice(i, Math.Min(bytesPerLine, data.Length - i));
            var hex = new StringBuilder(bytesPerLine * 3);
            var ascii = new StringBuilder(bytesPerLine);

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (j < line.Length)
                {
                    byte value = line[j];
                    hex.Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    ascii.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
                }
                else
                {
                    hex.Append("   ");
                    ascii.Append(' ');
                }
            }

            Console.WriteLine($"{baseOffset + i:X12}  {hex} |{ascii}|");
        }
    }

    private static (string ResourcePath, long Offset, int Length) ResolveTarget(
        string gameRoot,
        string dataDirectory,
        string target)
    {
        int lengthSeparator = target.LastIndexOf(':');
        int offsetSeparator = lengthSeparator > 0 ? target.LastIndexOf(':', lengthSeparator - 1) : -1;
        if (offsetSeparator <= 0 || lengthSeparator <= offsetSeparator + 1 || lengthSeparator == target.Length - 1)
        {
            throw new ArgumentException(
                "--peek-resource must be <file>:<offset>:<length>, for example resources.resource:19755776:64.");
        }

        string filePart = target[..offsetSeparator].Trim('"');
        string offsetPart = target[(offsetSeparator + 1)..lengthSeparator];
        string lengthPart = target[(lengthSeparator + 1)..];

        if (!long.TryParse(offsetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) || offset < 0)
            throw new ArgumentException($"Invalid resource offset: {offsetPart}");
        if (!int.TryParse(lengthPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length <= 0)
            throw new ArgumentException($"Invalid resource length: {lengthPart}");
        if (length > MaxPeekLength)
            throw new ArgumentException($"Resource peek length is capped at {MaxPeekLength} bytes.");

        string resourcePath;
        if (Path.IsPathRooted(filePart))
        {
            resourcePath = Path.GetFullPath(filePart);
        }
        else
        {
            string fromDataDirectory = Path.GetFullPath(Path.Combine(dataDirectory, filePart));
            string fromGameRoot = Path.GetFullPath(Path.Combine(gameRoot, filePart));
            resourcePath = File.Exists(fromDataDirectory) ? fromDataDirectory : fromGameRoot;
        }

        if (!File.Exists(resourcePath))
            throw new FileNotFoundException("Resource file not found", resourcePath);

        return (resourcePath, offset, length);
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
        Console.WriteLine("Peek usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- --adofai <dir> --peek-resource resources.resource:19755776:64");
    }

    private sealed record PeekOptions(string? GameRoot, string Target)
    {
        public static PeekOptions Parse(string[] args)
        {
            string? gameRoot = null;
            string? target = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--adofai":
                        gameRoot = RequireValue(args, ref i, arg);
                        break;
                    case "--peek-resource":
                        target = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown peek option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            if (target is null)
                throw new ArgumentException("Missing --peek-resource <file>:<offset>:<length>.");

            return new PeekOptions(gameRoot, target);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
