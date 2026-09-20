using System.Windows;
using System.Windows.Media.Imaging;

namespace ExtremeEditor.Wpf;

internal readonly record struct RasterChunkKey(
    int X,
    int Y,
    int ZoomBucket,
    long Generation);

internal sealed record RasterChunkResult(
    RasterChunkKey Key,
    Rect ScreenRect,
    BitmapSource Bitmap);

internal sealed class RasterChunkCache
{
    private readonly HashSet<RasterChunkKey> _pending = [];
    private readonly Dictionary<RasterChunkKey, RasterChunkResult> _ready = [];
    private int? _currentZoomBucket;

    public long CurrentGeneration { get; private set; }
    public int? CurrentZoomBucket => _currentZoomBucket;
    public int PendingCount => _pending.Count;
    public int ReadyCount => _ready.Count;

    public void Reset(long generation)
    {
        CurrentGeneration = generation;
        _currentZoomBucket = null;
        _pending.Clear();
        _ready.Clear();
    }

    public void Reset(long generation, int zoomBucket)
    {
        CurrentGeneration = generation;
        _currentZoomBucket = zoomBucket;
        _pending.Clear();
        _ready.Clear();
    }

    public bool TryMarkQueued(RasterChunkKey key)
    {
        if (!IsCurrent(key))
            return false;
        if (_ready.ContainsKey(key))
            return false;

        return _pending.Add(key);
    }

    public bool TryPublish(RasterChunkResult result)
    {
        if (!IsCurrent(result.Key))
            return false;
        if (!result.Bitmap.IsFrozen)
            return false;
        if (!_pending.Remove(result.Key))
            return false;

        _ready[result.Key] = result;
        return true;
    }

    public bool TryGetReady(RasterChunkKey key, out RasterChunkResult? result)
    {
        if (!IsCurrent(key))
        {
            result = null;
            return false;
        }

        return _ready.TryGetValue(key, out result);
    }

    public int EvictReadyOutside(IReadOnlySet<RasterChunkKey> desiredKeys)
    {
        ArgumentNullException.ThrowIfNull(desiredKeys);

        int evicted = 0;
        foreach (RasterChunkKey key in _ready.Keys.ToArray())
        {
            if (desiredKeys.Contains(key))
                continue;

            _ready.Remove(key);
            evicted++;
        }

        return evicted;
    }

    public int EvictPendingOutside(IReadOnlySet<RasterChunkKey> desiredKeys)
    {
        ArgumentNullException.ThrowIfNull(desiredKeys);

        int evicted = 0;
        foreach (RasterChunkKey key in _pending.ToArray())
        {
            if (desiredKeys.Contains(key))
                continue;

            _pending.Remove(key);
            evicted++;
        }

        return evicted;
    }

    public IReadOnlyList<RasterChunkKey> GetReadyKeys() => _ready.Keys.ToArray();

    private bool IsCurrent(RasterChunkKey key) =>
        key.Generation == CurrentGeneration &&
        (_currentZoomBucket is null || key.ZoomBucket == _currentZoomBucket.Value);
}
