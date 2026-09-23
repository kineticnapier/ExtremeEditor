using ExtremeEditor.AssetExtractor;

namespace ExtremeEditor.AssetExtractor.Tests;

internal static class Program
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static int Main(string[] args)
    {
        string? outputDirectory = null;
        try
        {
            string gameRoot = ResolveGameRoot(args);
            outputDirectory = Path.Combine(
                Path.GetTempPath(),
                $"ExtremeEditor-AssetExtractor-{Guid.NewGuid():N}");

            FloorTextureExtractionResult floorResult =
                AdoFaiFloorTextureExtractor.Extract(gameRoot, outputDirectory);

            VerifyCanonicalPng(floorResult.TilePath, "tile.png");
            VerifyCanonicalPng(floorResult.PerlinPath, "perlin.png");
            VerifyCanonicalPng(floorResult.RampPath, "ramp.png");
            VerifyCanonicalPng(floorResult.GlowPath, "glow.png");

            string iconOutput = Path.Combine(outputDirectory, "icons");
            IconExtractionResult iconResult =
                AdoFaiIconExtractor.Extract(gameRoot, iconOutput);

            VerifyDirectory(iconResult.FloorDirectory, Path.Combine(iconOutput, "floors"));
            VerifyDirectory(iconResult.OutlineDirectory, Path.Combine(iconOutput, "outlines"));
            VerifyDirectory(iconResult.EventDirectory, Path.Combine(iconOutput, "events"));

            VerifyCanonicalPng(Path.Combine(iconResult.FloorDirectory, "SwirlRed.png"), "SwirlRed.png");
            VerifyCanonicalPng(Path.Combine(iconResult.FloorDirectory, "SwirlBlue.png"), "SwirlBlue.png");
            VerifyCanonicalPng(Path.Combine(iconResult.FloorDirectory, "Rabbit.png"), "Rabbit.png");
            VerifyCanonicalPng(Path.Combine(iconResult.FloorDirectory, "Snail.png"), "Snail.png");

            if (iconResult.FloorIconCount < 4)
                throw new InvalidOperationException(
                    $"Expected at least four directly extracted floor icons, got {iconResult.FloorIconCount}.");

            Console.WriteLine("PASS: ADOFAI floor textures and representative floor icons were extracted directly into canonical PNG files.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory))
            {
                try
                {
                    Directory.Delete(outputDirectory, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private static string ResolveGameRoot(string[] args)
    {
        if (args.Length > 1)
            throw new ArgumentException("Usage: ExtremeEditor.AssetExtractor.Tests [adofai-root]");

        if (args.Length == 1)
            return ValidateGameRoot(args[0]);

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
            "ADOFAI installation was not found automatically. Pass the game root as the first argument.");
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
               File.Exists(Path.Combine(data, "resources.assets.resS"));
    }

    private static void VerifyDirectory(string actual, string expected)
    {
        if (!string.Equals(
                Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected canonical directory {expected}, got {actual}.");
        }
    }

    private static void VerifyCanonicalPng(string path, string expectedFileName)
    {
        if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected canonical file name {expectedFileName}, got {Path.GetFileName(path)}.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Expected extracted texture is missing: {path}", path);

        using FileStream stream = File.OpenRead(path);
        if (stream.Length <= PngSignature.Length)
            throw new InvalidDataException($"Extracted PNG is unexpectedly small: {path}");

        Span<byte> actual = stackalloc byte[PngSignature.Length];
        int read = stream.Read(actual);
        if (read != PngSignature.Length || !actual.SequenceEqual(PngSignature))
            throw new InvalidDataException($"Extracted file is not a PNG: {path}");
    }
}
