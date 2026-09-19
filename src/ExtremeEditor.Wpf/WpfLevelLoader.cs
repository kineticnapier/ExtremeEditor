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
    public static WpfLevelLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        LoadResult loaded = AdoFaiLoader.Load(path);

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
