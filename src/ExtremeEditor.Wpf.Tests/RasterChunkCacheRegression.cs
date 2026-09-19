using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class RasterChunkCacheRegression
{
    public static void Run()
    {
        VerifyDuplicateRequestsAreCoalesced();
        VerifyFrozenResultCanBePublished();
        VerifyResetRejectsStaleGeneration();
    }

    private static void VerifyDuplicateRequestsAreCoalesced()
    {
        var cache = new RasterChunkCache();
        cache.Reset(7);
        var key = new RasterChunkKey(1, 2, 28, 7);

        if (!cache.TryMarkQueued(key))
            throw new InvalidOperationException("First raster chunk request must be accepted.");
        if (cache.TryMarkQueued(key))
            throw new InvalidOperationException("Duplicate raster chunk requests must be coalesced.");
    }

    private static void VerifyFrozenResultCanBePublished()
    {
        var cache = new RasterChunkCache();
        cache.Reset(8);
        var key = new RasterChunkKey(3, 4, 28, 8);
        if (!cache.TryMarkQueued(key))
            throw new InvalidOperationException("Raster chunk request must be accepted before publication.");

        BitmapSource bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 255, 255, 255, 255 },
            4);
        bitmap.Freeze();

        var result = new RasterChunkResult(key, new Rect(0, 0, 1, 1), bitmap);
        if (!cache.TryPublish(result))
            throw new InvalidOperationException("Current-generation frozen raster chunk must publish.");
        if (!cache.TryGetReady(key, out RasterChunkResult? ready) || ready is null)
            throw new InvalidOperationException("Published raster chunk must be retrievable.");
        if (!ReferenceEquals(bitmap, ready.Bitmap) || !ready.Bitmap.IsFrozen)
            throw new InvalidOperationException("Raster cache must preserve the frozen bitmap instance.");
    }

    private static void VerifyResetRejectsStaleGeneration()
    {
        var cache = new RasterChunkCache();
        cache.Reset(8);
        var staleKey = new RasterChunkKey(1, 2, 28, 8);
        if (!cache.TryMarkQueued(staleKey))
            throw new InvalidOperationException("Initial current-generation request must be accepted.");

        cache.Reset(9);
        if (cache.TryMarkQueued(staleKey))
            throw new InvalidOperationException("Old-generation raster chunk request must be rejected after reset.");

        BitmapSource bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 255, 255, 255, 255 },
            4);
        bitmap.Freeze();
        var staleResult = new RasterChunkResult(staleKey, new Rect(0, 0, 1, 1), bitmap);
        if (cache.TryPublish(staleResult))
            throw new InvalidOperationException("Old-generation raster chunk result must be rejected after reset.");
        if (cache.GetReadyKeys().Count != 0)
            throw new InvalidOperationException("Reset raster cache must not expose stale ready chunks.");
    }
}
