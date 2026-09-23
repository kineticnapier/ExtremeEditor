namespace ExtremeEditor.AssetExtractor;

public sealed record IconExtractionResult(
    string OutputDirectory,
    string FloorDirectory,
    string OutlineDirectory,
    string EventDirectory,
    int FloorIconCount,
    int OutlineIconCount,
    int EventIconCount);

public static class AdoFaiIconExtractor
{
    public static IconExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        throw new NotSupportedException(
            "RED: direct ADOFAI icon extraction is not implemented yet.");
    }
}
