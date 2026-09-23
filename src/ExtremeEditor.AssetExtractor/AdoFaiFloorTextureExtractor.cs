namespace ExtremeEditor.AssetExtractor;

public sealed record FloorTextureExtractionResult(
    string OutputDirectory,
    string TilePath,
    string PerlinPath,
    string RampPath,
    string GlowPath);

public static class AdoFaiFloorTextureExtractor
{
    public static FloorTextureExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        throw new NotSupportedException(
            "RED: direct ADOFAI floor texture extraction is not implemented yet.");
    }
}
