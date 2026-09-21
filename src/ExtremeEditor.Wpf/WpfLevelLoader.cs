using System.Diagnostics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed record WpfLevelLoadResult(
    LevelDocument Document,
    SpatialGridIndex Index,
    LoadMetrics Metrics,
    TimeSpan IndexTime);

internal static class WpfLevelLoader
{
    public static WpfLevelLoadResult Load(string path) =>
        LoadAsync(path).GetAwaiter().GetResult();

    public static async Task<WpfLevelLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        LoadResult loaded = await AdoFaiLoader.LoadFlatAsync(path, cancellationToken)
            .ConfigureAwait(false);

        // The flat core loader intentionally keeps only timing/gameplay fields.
        // Read the small subset of track-colour metadata required by the renderer
        // in a second streaming pass; it never materializes angleData/actions as a
        // full JSON DOM, so pathological charts remain bounded in memory.
        TrackColorSourceData trackColors = TrackColorSourceReader.Load(path, cancellationToken);
        TrackColorMetadataCache.Attach(loaded.Document, trackColors);

        var indexWatch = Stopwatch.StartNew();
        var index = new SpatialGridIndex(loaded.Document.Positions);
        indexWatch.Stop();

        return new WpfLevelLoadResult(
            loaded.Document,
            index,
            loaded.Metrics,
            indexWatch.Elapsed);
    }
}
