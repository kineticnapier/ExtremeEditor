using System.Text.Json;

namespace ExtremeEditor.AssetExtractor.Cli.Tests;

internal static class Program
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] RiffSignature = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WaveSignature = [(byte)'W', (byte)'A', (byte)'V', (byte)'E'];

    public static int Main(string[] args)
    {
        string? outputDirectory = null;
        try
        {
            string gameRoot = ResolveGameRoot(args);
            outputDirectory = Path.Combine(
                Path.GetTempPath(),
                $"ExtremeEditor-AssetExtractor-Cli-{Guid.NewGuid():N}");

            int exitCode = global::ExtremeEditor.AssetExtractor.Program.Main([gameRoot, outputDirectory]);
            if (exitCode != 0)
                throw new InvalidOperationException($"AssetExtractor CLI returned exit code {exitCode}.");

            string floorDirectory = Path.Combine(outputDirectory, "floor-mesh");
            string iconDirectory = Path.Combine(outputDirectory, "icons");
            string floorIconDirectory = Path.Combine(iconDirectory, "floors");
            string outlineDirectory = Path.Combine(iconDirectory, "outlines");
            string eventDirectory = Path.Combine(iconDirectory, "events");
            string categoryDirectory = Path.Combine(iconDirectory, "categories");
            string hitSoundDirectory = Path.Combine(outputDirectory, "hitsounds");
            string manifestPath = Path.Combine(outputDirectory, "manifest.json");

            VerifyDirectory(floorDirectory);
            VerifyDirectory(floorIconDirectory);
            VerifyDirectory(outlineDirectory);
            VerifyDirectory(eventDirectory);
            VerifyDirectory(categoryDirectory);
            VerifyDirectory(hitSoundDirectory);

            VerifyPng(Path.Combine(floorDirectory, "tile.png"));
            VerifyPng(Path.Combine(floorDirectory, "perlin.png"));
            VerifyPng(Path.Combine(floorDirectory, "ramp.png"));
            VerifyPng(Path.Combine(floorDirectory, "glow.png"));

            int floorIcons = CountFiles(floorIconDirectory, "*.png");
            int outlineIcons = CountFiles(outlineDirectory, "*.png");
            int eventIcons = CountFiles(eventDirectory, "*.png");
            int categoryIcons = CountFiles(categoryDirectory, "*.png");
            int hitSounds = CountFiles(hitSoundDirectory, "*.wav");

            if (floorIcons <= 4)
                throw new InvalidOperationException($"Expected full floor icon catalog, got {floorIcons} PNGs.");
            if (outlineIcons == 0)
                throw new InvalidOperationException("Expected floor outline icons, got 0.");
            if (eventIcons == 0)
                throw new InvalidOperationException("Expected LevelEventType icons, got 0.");
            if (categoryIcons == 0)
                throw new InvalidOperationException("Expected LevelEventCategory icons, got 0.");
            if (hitSounds <= 1)
                throw new InvalidOperationException($"Expected full HitSound set, got {hitSounds} WAVs.");

            foreach (string path in Directory.EnumerateFiles(hitSoundDirectory, "*.wav"))
                VerifyWave(path);

            VerifyManifest(
                manifestPath,
                floorIcons,
                outlineIcons,
                eventIcons,
                categoryIcons,
                hitSounds);

            Console.WriteLine(
                $"PASS: standalone AssetExtractor CLI wrote canonical cache + manifest " +
                $"(floors={floorIcons}, outlines={outlineIcons}, events={eventIcons}, categories={categoryIcons}, hitsounds={hitSounds}).");
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

    private static void VerifyManifest(
        string path,
        int floorIcons,
        int outlineIcons,
        int eventIcons,
        int categoryIcons,
        int hitSounds)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Expected manifest.json from standalone AssetExtractor CLI.", path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;

        if (root.GetProperty("formatVersion").GetInt32() != 1)
            throw new InvalidDataException("manifest.json formatVersion must be 1.");

        string gameVersion = root.GetProperty("gameVersion").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(gameVersion))
            throw new InvalidDataException("manifest.json gameVersion is empty.");

        string fingerprint = root.GetProperty("sourceFingerprint").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidDataException("manifest.json sourceFingerprint is empty.");

        VerifyCount(root, "floorIcons", floorIcons);
        VerifyCount(root, "outlineIcons", outlineIcons);
        VerifyCount(root, "eventIcons", eventIcons);
        VerifyCount(root, "categoryIcons", categoryIcons);
        VerifyCount(root, "hitSounds", hitSounds);
    }

    private static void VerifyCount(JsonElement root, string propertyName, int expected)
    {
        int actual = root.GetProperty(propertyName).GetInt32();
        if (actual != expected)
            throw new InvalidDataException(
                $"manifest.json {propertyName} mismatch: manifest={actual}, files={expected}.");
    }

    private static void VerifyDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Expected canonical cache directory: {path}");
    }

    private static int CountFiles(string directory, string pattern)
        => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count();

    private static void VerifyPng(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Expected PNG is missing: {path}", path);

        using FileStream stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
            throw new InvalidDataException($"File is not a PNG: {path}");
    }

    private static void VerifyWave(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length <= 44)
            throw new InvalidDataException($"WAV is unexpectedly small: {path}");

        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length ||
            !header[..4].SequenceEqual(RiffSignature) ||
            !header.Slice(8, 4).SequenceEqual(WaveSignature))
        {
            throw new InvalidDataException($"File is not RIFF/WAVE: {path}");
        }
    }

    private static string ResolveGameRoot(string[] args)
    {
        if (args.Length > 1)
            throw new ArgumentException("Usage: ExtremeEditor.AssetExtractor.Cli.Tests [adofai-root]");

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
}
