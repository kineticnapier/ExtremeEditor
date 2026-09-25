using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class RasterChunkWorkerRegression
{
    public static void Run()
    {
        VerifyViewportExposesRasterWorkerShutdown();
        VerifyWorkerBuildsFrozenBitmapOffCallerThread();
        VerifyImmediateDisposeWithQueuedWorkTerminates();
        VerifyDisposeIsIdempotentWithQueuedWork();
    }

    private static void VerifyViewportExposesRasterWorkerShutdown()
    {
        MethodInfo? shutdown = typeof(LevelViewport).GetMethod(
            "ShutdownRasterWorker",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (shutdown is null)
        {
            throw new InvalidOperationException(
                "LevelViewport.ShutdownRasterWorker is missing. Window shutdown must explicitly stop the raster worker.");
        }
    }

    private static void VerifyWorkerBuildsFrozenBitmapOffCallerThread()
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        using var worker = new RasterChunkWorker();

        RasterChunkRequest request = CreateRequest(new RasterChunkKey(0, 0, 28, 1));

        var enqueueWatch = Stopwatch.StartNew();
        worker.Enqueue(request);
        enqueueWatch.Stop();

        if (enqueueWatch.ElapsedMilliseconds >= 33)
            throw new InvalidOperationException($"RasterChunkWorker.Enqueue blocked caller for {enqueueWatch.ElapsedMilliseconds} ms.");

        RasterChunkResult? result = null;
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (worker.TryDequeueCompleted(out result))
                break;
            Thread.Sleep(5);
        }

        if (result is null)
            throw new InvalidOperationException("Raster worker did not complete tiny chunk within timeout.");
        if (!result.Bitmap.IsFrozen)
            throw new InvalidOperationException("Worker bitmap must be frozen before publication.");
        if (worker.LastBuildThreadId == callerThreadId)
            throw new InvalidOperationException("Raster worker rendered on the caller/UI thread.");
        if (worker.RequestedCount != 1 || worker.CompletedCount != 1)
            throw new InvalidOperationException($"Unexpected worker diagnostics: requested={worker.RequestedCount}, completed={worker.CompletedCount}.");
    }

    private static void VerifyImmediateDisposeWithQueuedWorkTerminates()
    {
        var watch = Stopwatch.StartNew();
        var worker = new RasterChunkWorker();
        worker.Enqueue(CreateRequest(new RasterChunkKey(1, 0, 28, 1)));
        worker.Dispose();
        watch.Stop();

        if (watch.Elapsed >= TimeSpan.FromSeconds(3))
            throw new InvalidOperationException($"Raster worker disposal hung for {watch.Elapsed.TotalMilliseconds:F0} ms with queued work.");
    }

    private static void VerifyDisposeIsIdempotentWithQueuedWork()
    {
        var watch = Stopwatch.StartNew();
        var worker = new RasterChunkWorker();
        for (int i = 0; i < 8; i++)
            worker.Enqueue(CreateRequest(new RasterChunkKey(i, 1, 28, 1)));

        worker.Dispose();
        worker.Dispose();
        watch.Stop();

        if (watch.Elapsed >= TimeSpan.FromSeconds(3))
            throw new InvalidOperationException($"Repeated raster worker disposal hung for {watch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static RasterChunkRequest CreateRequest(RasterChunkKey key)
    {
        Vector2[] positions =
        [
            Vector2.Zero,
            Vector2.UnitX
        ];
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
}
