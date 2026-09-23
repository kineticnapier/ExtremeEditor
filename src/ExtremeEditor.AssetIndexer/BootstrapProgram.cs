using System.IO.Compression;

namespace ExtremeEditor.AssetIndexer;

internal static class BootstrapProgram
{
    private const string ClassDataArtifactUrl =
        "https://nightly.link/AssetRipper/Tpk/workflows/type_tree_tpk/master/lz4_file.zip";

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(static arg => arg is "-h" or "--help") ||
            HasOption(args, "--classdata"))
        {
            return Program.Main(args);
        }

        try
        {
            string classDataPath = await EnsureClassDataAsync();
            Console.WriteLine($"[index] classdata-cache={classDataPath}");
            return Program.Main([.. args, "--classdata", classDataPath]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[index] classdata auto-download failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(
                "[index] continuing without classdata; pass --classdata <classdata.tpk> to override manually.");
            return Program.Main(args);
        }
    }

    private static bool HasOption(string[] args, string option)
        => args.Any(arg => string.Equals(arg, option, StringComparison.Ordinal));

    private static async Task<string> EnsureClassDataAsync()
    {
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExtremeEditor",
            "AssetIndexer");
        Directory.CreateDirectory(cacheDirectory);

        string targetPath = Path.Combine(cacheDirectory, "classdata.tpk");
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            return targetPath;

        string zipPath = Path.Combine(cacheDirectory, $"classdata-{Guid.NewGuid():N}.zip");
        string extractedPath = Path.Combine(cacheDirectory, $"classdata-{Guid.NewGuid():N}.tpk");

        try
        {
            Console.WriteLine("[index] classdata cache missing; downloading AssetRipper LZ4 type-tree package...");

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExtremeEditor.AssetIndexer/1.0");

            using (HttpResponseMessage response = await client.GetAsync(
                       ClassDataArtifactUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using Stream input = await response.Content.ReadAsStreamAsync();
                await using FileStream output = File.Create(zipPath);
                await input.CopyToAsync(output);
            }

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry? tpkEntry = archive.Entries.FirstOrDefault(entry =>
                    entry.FullName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase));
                if (tpkEntry is null)
                    throw new InvalidDataException("Downloaded classdata artifact contains no .tpk file.");

                await using Stream input = tpkEntry.Open();
                await using FileStream output = File.Create(extractedPath);
                await input.CopyToAsync(output);
            }

            if (!File.Exists(extractedPath) || new FileInfo(extractedPath).Length == 0)
                throw new InvalidDataException("Extracted classdata.tpk is empty.");

            File.Move(extractedPath, targetPath, true);
            Console.WriteLine($"[index] classdata cached at {targetPath}");
            return targetPath;
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(extractedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
