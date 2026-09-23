namespace ExtremeEditor.AssetExtractor;

public sealed record HitSoundExtractionResult(
    string OutputDirectory,
    string KickPath,
    int HitSoundCount);

public static class AdoFaiHitSoundExtractor
{
    public static HitSoundExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        throw new NotSupportedException(
            "RED: direct ADOFAI FSB5 hitsound extraction is not implemented yet.");
    }
}
