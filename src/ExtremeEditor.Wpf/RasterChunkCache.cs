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

        _pending.Remove(result.Key);
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

    public IReadOnlyList<RasterChunkKey> GetReadyKeys() => _ready.Keys.ToArray();

    private bool IsCurrent(RasterChunkKey key) =>
        key.Generation == CurrentGeneration &&
        (_currentZoomBucket is null || key.ZoomBucket == _currentZoomBucket.Value);
}
