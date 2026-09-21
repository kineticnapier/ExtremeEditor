using System.Diagnostics;
using ExtremeEditor.Core;

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
