namespace ExtremeEditor.Rendering;

public sealed record AssetImportResult(string CacheDirectory, int ImportedCount, IReadOnlyList<string> MissingAssets)
{
    public bool HasFloorTexture => !MissingAssets.Contains(AssetCache.TileFileName, StringComparer.OrdinalIgnoreCase);
}

public static class AssetCache
{
    public const string TileFileName = "tile.png";
    public const string PerlinFileName = "perlin.png";
    public const string RampFileName = "ramp.png";
    public const string GlowFileName = "glow.png";

    public static string FloorMeshDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "floor-mesh");

    public static AssetImportResult ImportProbeFolder(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);

        Directory.CreateDirectory(FloorMeshDirectory);
        var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*.png", SearchOption.AllDirectories))
        {
            string? canonical = Classify(Path.GetFileName(source));
            if (canonical is null || imported.Contains(canonical))
                continue;

            File.Copy(source, Path.Combine(FloorMeshDirectory, canonical), overwrite: true);
            imported.Add(canonical);
        }

        string[] expected = [TileFileName, PerlinFileName, RampFileName, GlowFileName];
        string[] missing = expected.Where(name => !File.Exists(Path.Combine(FloorMeshDirectory, name))).ToArray();
        return new AssetImportResult(FloorMeshDirectory, imported.Count, missing);
    }

    public static string PathFor(string canonicalName) => Path.Combine(FloorMeshDirectory, canonicalName);

    private static string? Classify(string fileName)
    {
        if (fileName.Contains("_TileTex-", StringComparison.OrdinalIgnoreCase)) return TileFileName;
        if (fileName.Contains("_PerlinTex-", StringComparison.OrdinalIgnoreCase)) return PerlinFileName;
        if (fileName.Contains("_MainTex-", StringComparison.OrdinalIgnoreCase)) return RampFileName;
        if (fileName.Contains("light_white", StringComparison.OrdinalIgnoreCase)) return GlowFileName;
        return null;
    }
}
