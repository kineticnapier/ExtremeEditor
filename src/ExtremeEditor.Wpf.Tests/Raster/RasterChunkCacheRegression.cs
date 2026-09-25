using System.Reflection;
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
        VerifyZoomResetRejectsStaleResult();
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

        BitmapSource bitmap = CreateFrozenBitmap();
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

        var staleResult = new RasterChunkResult(staleKey, new Rect(0, 0, 1, 1), CreateFrozenBitmap());
        if (cache.TryPublish(staleResult))
            throw new InvalidOperationException("Old-generation raster chunk result must be rejected after reset.");
        if (cache.GetReadyKeys().Count != 0)
            throw new InvalidOperationException("Reset raster cache must not expose stale ready chunks.");
    }

    private static void VerifyZoomResetRejectsStaleResult()
    {
        MethodInfo? reset = typeof(RasterChunkCache).GetMethod(
            "Reset",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(int)],
            modifiers: null);

        if (reset is null)
        {
            throw new InvalidOperationException(
                "RasterChunkCache.Reset(long generation, int zoomBucket) is missing. Zoom changes must invalidate stale chunk results even within the same generation.");
        }

        var cache = new RasterChunkCache();
        reset.Invoke(cache, [10L, 28]);
        var staleZoomKey = new RasterChunkKey(2, 3, 28, 10);
        if (!cache.TryMarkQueued(staleZoomKey))
            throw new InvalidOperationException("Current zoom-bucket chunk request must be accepted.");

        reset.Invoke(cache, [10L, 29]);
        var staleResult = new RasterChunkResult(staleZoomKey, new Rect(0, 0, 1, 1), CreateFrozenBitmap());
        if (cache.TryPublish(staleResult))
            throw new InvalidOperationException("Old zoom-bucket raster result must be rejected after zoom reset.");
        if (cache.TryGetReady(staleZoomKey, out _))
            throw new InvalidOperationException("Old zoom-bucket raster result must not become ready after zoom reset.");
        if (cache.GetReadyKeys().Count != 0)
            throw new InvalidOperationException("Zoom reset must not expose stale ready chunks.");
    }

    private static BitmapSource CreateFrozenBitmap()
    {
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
        return bitmap;
    }
}
