using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            VerifyFloorGeometryIsCachedAndFrozen();
            VerifyIconBitmapIsCachedAndFrozen();
            OpenLevelRegression.Run();
            PlaybackSetupRegression.Run();
            PlaybackViewportRegression.Run();
            FloorRendererThreadingRegression.Run();
            RasterChunkWorkerRegression.Run();
            PerformanceRegression.Run();
            DenseIconRegression.Run();
            RasterChunkCacheRegression.Run();
            RasterChunkStreamingRegression.Run();
            PlaybackRasterPrefetchRegression.Run();
            PlaybackDiagnosticsRegression.Run();
            Console.WriteLine("PASS: WPF floor geometry, icon bitmaps, level open, playback setup, playback viewport, raster cache, raster streaming, raster worker, threading, playback diagnostics, and performance regressions are valid.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void VerifyFloorGeometryIsCachedAndFrozen()
    {
        Assembly assembly = typeof(LevelViewport).Assembly;
        Type rendererType = assembly.GetType("ExtremeEditor.Wpf.WpfFloorRenderer")
            ?? throw new InvalidOperationException("WpfFloorRenderer does not exist yet.");

        MethodInfo method = rendererType.GetMethod(
            "GetCachedGeometry",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfFloorRenderer.GetCachedGeometry is missing.");

        object? first = method.Invoke(null, [0f, MathF.PI / 2f, false]);
        object? second = method.Invoke(null, [0f, MathF.PI / 2f, false]);

        if (first is not StreamGeometry geometry)
            throw new InvalidOperationException("GetCachedGeometry must return StreamGeometry.");

        if (!ReferenceEquals(first, second))
            throw new InvalidOperationException("Repeated geometry requests must reuse the cached StreamGeometry instance.");

        if (!geometry.IsFrozen)
            throw new InvalidOperationException("Cached StreamGeometry must be frozen before reuse.");

        Rect bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Cached floor geometry must have non-empty bounds.");
    }

    private static void VerifyIconBitmapIsCachedAndFrozen()
    {
        Assembly assembly = typeof(LevelViewport).Assembly;
        Type rendererType = assembly.GetType("ExtremeEditor.Wpf.WpfIconRenderer")
            ?? throw new InvalidOperationException("WpfIconRenderer does not exist yet.");

        MethodInfo method = rendererType.GetMethod(
            "GetCachedBitmap",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfIconRenderer.GetCachedBitmap is missing.");

        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-WpfIconRenderer-{Guid.NewGuid():N}.png");
        try
        {
            WriteOnePixelPng(path);
            object? first = method.Invoke(null, [path]);
            object? second = method.Invoke(null, [path]);

            if (first is not BitmapSource bitmap)
                throw new InvalidOperationException("GetCachedBitmap must return BitmapSource for a valid PNG.");

            if (!ReferenceEquals(first, second))
                throw new InvalidOperationException("Repeated icon requests must reuse the cached BitmapSource instance.");

            if (!bitmap.IsFrozen)
                throw new InvalidOperationException("Cached icon BitmapSource must be frozen before reuse.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void WriteOnePixelPng(string path)
    {
        BitmapSource source = BitmapSource.Create(
            1, 1, 96, 96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 255, 255, 255, 255 },
            4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
