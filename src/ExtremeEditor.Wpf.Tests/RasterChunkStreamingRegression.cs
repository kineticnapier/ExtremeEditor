using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class RasterChunkStreamingRegression
{
    public static void Run()
    {
        RequireViewportStreamingDiagnostics();
        VerifyReadyCacheCanBeBoundedToDesiredSet();
        VerifyWorkerSkipsUndesiredQueuedRequests();
    }

    private static void RequireViewportStreamingDiagnostics()
    {
        RequireIntProperty(typeof(LevelViewport), "RasterReadyChunkCount");
        RequireIntProperty(typeof(LevelViewport), "RasterPendingChunkCount");
        RequireIntProperty(typeof(LevelViewport), "RasterChunksCanceled");
    }

    private static void VerifyReadyCacheCanBeBoundedToDesiredSet()
    {
        MethodInfo evict = typeof(RasterChunkCache).GetMethod(
            "EvictReadyOutside",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IReadOnlySet<RasterChunkKey>)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "RasterChunkCache.EvictReadyOutside(IReadOnlySet<RasterChunkKey>) is missing. Ready raster memory must be bounded to the current streaming window.");

        PropertyInfo readyCount = typeof(RasterChunkCache).GetProperty(
            "ReadyCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RasterChunkCache.ReadyCount is missing.");

        var cache = new RasterChunkCache();
        cache.Reset(21, 28);
        BitmapSource bitmap = CreateFrozenBitmap();

        for (int x = 0; x < 200; x++)
        {
            var key = new RasterChunkKey(x, 0, 28, 21);
            if (!cache.TryMarkQueued(key))
                throw new InvalidOperationException($"Failed to queue synthetic raster chunk {x}.");
            if (!cache.TryPublish(new RasterChunkResult(key, new Rect(x, 0, 1, 1), bitmap)))
                throw new InvalidOperationException($"Failed to publish synthetic raster chunk {x}.");
        }

        var desired = new HashSet<RasterChunkKey>();
        for (int x = 90; x < 110; x++)
            desired.Add(new RasterChunkKey(x, 0, 28, 21));

        object? evictedValue = evict.Invoke(cache, [desired]);
        int evicted = evictedValue is int count
            ? count
            : throw new InvalidOperationException("EvictReadyOutside must return the number of evicted ready chunks.");
        int remaining = readyCount.GetValue(cache) is int ready
            ? ready
            : throw new InvalidOperationException("RasterChunkCache.ReadyCount must be an int.");

        if (evicted != 180 || remaining != 20)
        {
            throw new InvalidOperationException(
                $"Raster ready cache was not bounded to the desired set: evicted={evicted}, remaining={remaining}.");
        }

        foreach (RasterChunkKey key in cache.GetReadyKeys())
        {
            if (!desired.Contains(key))
                throw new InvalidOperationException($"Evicted raster chunk remained ready: {key}.");
        }
    }

    private static void VerifyWorkerSkipsUndesiredQueuedRequests()
    {
        MethodInfo setDesired = typeof(RasterChunkWorker).GetMethod(
            "SetDesiredKeys",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IReadOnlySet<RasterChunkKey>)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "RasterChunkWorker.SetDesiredKeys(IReadOnlySet<RasterChunkKey>) is missing. Obsolete queued raster work must be discarded before rendering.");

        PropertyInfo canceledProperty = typeof(RasterChunkWorker).GetProperty(
            "CanceledCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RasterChunkWorker.CanceledCount is missing.");

        using var worker = new RasterChunkWorker();
        var desiredKey = new RasterChunkKey(99, 0, 28, 1);
        IReadOnlySet<RasterChunkKey> desired = new HashSet<RasterChunkKey> { desiredKey };
        setDesired.Invoke(worker, [desired]);

        for (int x = 0; x < 100; x++)
            worker.Enqueue(CreateRequest(new RasterChunkKey(x, 0, 28, 1)));

        var timeout = Stopwatch.StartNew();
        int canceled = 0;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            canceled = canceledProperty.GetValue(worker) is int value ? value : 0;
            if (canceled >= 99)
                break;
            Thread.Sleep(5);
        }

        if (canceled < 99)
        {
            throw new InvalidOperationException(
                $"Raster worker rendered or retained obsolete queued work instead of canceling it: canceled={canceled}, requested={worker.RequestedCount}.");
        }
        if (worker.CompletedCount > 1)
        {
            throw new InvalidOperationException(
                $"Raster worker completed obsolete requests despite a one-key desired set: completed={worker.CompletedCount}.");
        }
    }

    private static RasterChunkRequest CreateRequest(RasterChunkKey key)
    {
        Vector2[] positions = [Vector2.Zero, Vector2.UnitX];
        double[] angles = [0.0];
        return new RasterChunkRequest(
            key,
            new Rect(-1, -1, 2, 2),
            new Rect(0, 0, 64, 64),
            64,
            64,
            28f,
            [0],
            positions,
            angles);
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

    private static void RequireIntProperty(Type type, string name)
    {
        PropertyInfo? property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null)
            throw new InvalidOperationException($"{type.Name}.{name} is missing.");
        if (property.PropertyType != typeof(int))
            throw new InvalidOperationException($"{type.Name}.{name} must be Int32.");
    }
}
