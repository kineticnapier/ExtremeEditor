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
            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_UI_CLEANUP_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                AssetSetupUiRegression.Run();
                PlaybackDiagnosticsLayoutRegression.Run();
                NativeOnlyViewportRegression.Run();
                Console.WriteLine("PASS: UI cleanup regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_WINFORMS_REMOVAL_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                WinFormsHostRemovalRegression.Run();
                Console.WriteLine("PASS: legacy WinForms host removal regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SELECTION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EditorSelectionStateRegression.Run();
                Console.WriteLine("PASS: editor selection-state regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_NATIVE_ONLY_VIEWPORT_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                NativeOnlyViewportRegression.Run();
                Console.WriteLine("PASS: native-only production viewport regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_ASSET_SETUP_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                AssetSetupRegression.Run();
                AdoFaiInstallationLocatorRegression.Run();
                AssetSetupUiRegression.Run();
                LegacyAssetImportRemovalRegression.Run();
                Console.WriteLine("PASS: WPF asset setup + ADOFAI installation locator + setup UI + legacy import removal regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_LOAD_PREP_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                LoadPreparationParallelismRegression.Run();
                ProgressiveLoadReadinessRegression.Run();
                Console.WriteLine("PASS: load-preparation regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CAMERA_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CameraRuntimeTileReferenceRegression.Run();
                CameraTileMoveTrackFreezeRegression.Run();
                CameraPlayerDoubleRelativeRegression.Run();
                CameraPlayerSmoothPivotRegression.Run();
                Console.WriteLine("PASS: camera-only regressions are valid.");
                return 0;
            }

            WinFormsHostRemovalRegression.Run();
            EditorSelectionStateRegression.Run();
            NativeOnlyViewportRegression.Run();
            PlaybackDiagnosticsLayoutRegression.Run();
            AssetSetupRegression.Run();
            AdoFaiInstallationLocatorRegression.Run();
            AssetSetupUiRegression.Run();
            LegacyAssetImportRemovalRegression.Run();
            LoadPreparationParallelismRegression.Run();
            ProgressiveLoadReadinessRegression.Run();
            VerifyFloorGeometryIsCachedAndFrozen();
            VerifyIconBitmapIsCachedAndFrozen();
            OpenLevelRegression.Run();
            OpenDirtyDocumentRegression.Run();
            PlaybackSetupRegression.Run();
            PlaybackViewportRegression.Run();
            FloorRendererThreadingRegression.Run();
            RasterChunkWorkerRegression.Run();
            PerformanceRegression.Run();
            DenseIconRegression.Run();
            RasterChunkCacheRegression.Run();
            RasterChunkStreamingRegression.Run();
            RasterSceneRebaseRegression.Run();
            PlaybackRasterPrefetchRegression.Run();
            PlaybackDiagnosticsRegression.Run();
            PlaybackStallDiagnosticsRegression.Run();
            PlaybackLookaheadDiagnosticsRegression.Run();
            TemporalPlaybackRenderingRegression.Run();
            TemporalPlaybackRoutingRegression.Run();
            TemporalPlaybackIconDiagnosticsRegression.Run();
            TemporalPlaybackRetentionRegression.Run();
            NativeRendererAbiRegression.Run();
            NativeRendererHostRegression.Run();
            CameraRuntimeTileReferenceRegression.Run();
            CameraTileMoveTrackFreezeRegression.Run();
            CameraPlayerDoubleRelativeRegression.Run();
            CameraPlayerSmoothPivotRegression.Run();
            Console.WriteLine("PASS: legacy WinForms host removal, editor selection state, native-only production viewport, WPF asset setup/ADOFAI locator/setup UI/legacy import removal, floor geometry, icon bitmaps, level open/dirty-open guard, playback setup, playback viewport, raster cache, raster streaming, raster scene rebase, raster worker, threading, playback diagnostics/layout/stall/lookahead, temporal playback rendering/routing/icon diagnostics/retention, native renderer ABI/host, runtime Tile camera reference, MoveTrack Tile camera freeze, Player camera reference conversion/smooth pivot, load preparation/progressive readiness, and performance regressions are valid.");
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
            throw new InvalidOperationException("Cached StreamGeometry must have non-empty bounds.");
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
                throw new InvalidOperationException("Repeated icon bitmap requests must reuse the cached BitmapSource instance.");

            if (!bitmap.IsFrozen)
                throw new InvalidOperationException("Cached BitmapSource must be frozen before reuse.");
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
