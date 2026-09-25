using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Media;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class FloorRendererThreadingRegression
{
    public static void Run()
    {
        VerifyGeometryCacheHasSynchronizationGate();
        VerifyGeometryCacheCanBeUsedFromMultipleStaThreads();
    }

    private static void VerifyGeometryCacheHasSynchronizationGate()
    {
        Type rendererType = typeof(LevelViewport).Assembly.GetType("ExtremeEditor.Wpf.WpfFloorRenderer")
            ?? throw new InvalidOperationException("WpfFloorRenderer does not exist.");

        FieldInfo? gate = rendererType.GetField(
            "GeometryCacheGate",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (gate is null)
        {
            throw new InvalidOperationException(
                "WpfFloorRenderer.GeometryCacheGate is missing. The shared geometry cache must synchronize worker/UI access before raster work moves to another STA thread.");
        }
    }

    private static void VerifyGeometryCacheCanBeUsedFromMultipleStaThreads()
    {
        Type rendererType = typeof(LevelViewport).Assembly.GetType("ExtremeEditor.Wpf.WpfFloorRenderer")
            ?? throw new InvalidOperationException("WpfFloorRenderer does not exist.");
        MethodInfo getGeometry = rendererType.GetMethod(
            "GetCachedGeometry",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfFloorRenderer.GetCachedGeometry is missing.");

        var errors = new ConcurrentQueue<Exception>();
        Thread first = CreateStaThread(() => ExerciseGeometryCache(getGeometry, 0, errors));
        Thread second = CreateStaThread(() => ExerciseGeometryCache(getGeometry, 1, errors));

        first.Start();
        second.Start();
        first.Join();
        second.Join();

        if (errors.TryDequeue(out Exception? error))
            throw new InvalidOperationException("Concurrent STA geometry-cache access failed.", error);
    }

    private static Thread CreateStaThread(ThreadStart action)
    {
        var thread = new Thread(action)
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        return thread;
    }

    private static void ExerciseGeometryCache(
        MethodInfo getGeometry,
        int phase,
        ConcurrentQueue<Exception> errors)
    {
        try
        {
            for (int i = 0; i < 256; i++)
            {
                float entry = ((i + phase) % 16) * MathF.PI / 8f;
                float exit = entry + ((i % 7) + 1) * MathF.PI / 8f;
                bool midSpin = (i & 1) != 0;

                object? value = getGeometry.Invoke(null, [entry, exit, midSpin]);
                if (value is not StreamGeometry geometry || !geometry.IsFrozen)
                    throw new InvalidOperationException("Cached floor geometry must be a frozen StreamGeometry.");
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            errors.Enqueue(ex.InnerException);
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex);
        }
    }
}
