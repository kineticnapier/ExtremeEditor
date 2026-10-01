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
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_RECOLOR_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                RecolorTrackRuntimeSemanticsRegression.Run();
                Console.WriteLine("PASS: RecolorTrack runtime semantics regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TRACK_VFX_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TrackVisualCompatibilityRegression.Run();
                Console.WriteLine("PASS: Track VFX compatibility regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_NATIVE_SHUTDOWN_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                NativeRendererHostRegression.Run();
                Console.WriteLine("PASS: native renderer shutdown lifecycle regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_TRANSFORM_INTEGRATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                DecorationTransformRendererIntegrationRegression.Run();
                Console.WriteLine("PASS: Decoration renderer transform integration regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_TARGETING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsTargetingRegression.Run();
                Console.WriteLine("PASS: MoveDecorations targeting regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_RENDERER_INTEGRATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsRendererIntegrationRegression.Run();
                Console.WriteLine("PASS: MoveDecorations renderer integration regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_TILE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsTilePlacementRegression.Run();
                Console.WriteLine("PASS: MoveDecorations Tile placement regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_COORDINATES_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsCoordinateRegression.Run();
                Console.WriteLine("PASS: MoveDecorations coordinate regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_EASING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsEasingRegression.Run();
                Console.WriteLine("PASS: MoveDecorations easing regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_INTERPOLATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsInterpolationRegression.Run();
                Console.WriteLine("PASS: MoveDecorations interpolation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MOVE_DECORATIONS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MoveDecorationsEvaluationRegression.Run();
                Console.WriteLine("PASS: MoveDecorations evaluation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VFX_TIMELINE_CORE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VfxTimelineCoreRegression.Run();
                VfxTimelineExpansionRegression.Run();
                VfxTimelineEdgeRegression.Run();
                Console.WriteLine("PASS: VFX timeline core regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TWIRL_INCREMENTAL_NATIVE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TwirlIncrementalNativeRegression.Run();
                Console.WriteLine("PASS: incremental Twirl native action-edit regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MANAGED_GEOMETRY_EDIT_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                ManagedGeometryEditRegression.Run();
                Console.WriteLine("PASS: managed geometry action-edit regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_INCREMENTAL_NATIVE_EDIT_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                IncrementalNativeEditRegression.Run();
                Console.WriteLine("PASS: incremental native action-edit regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SAVE_OWNERSHIP_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                SaveNodeOwnershipRegression.Run();
                LegacyEventRoundTripRegression.Run();
                Console.WriteLine("PASS: save JsonNode ownership and round-trip regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_GO_TO_FLOOR_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                GoToFloorRegression.Run();
                Console.WriteLine("PASS: Go to Floor regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TRACK_TRANSFORM_SCALING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TrackTransformScalingRegression.Run();
                Console.WriteLine("PASS: persistent track-transform scaling regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_ASSET_EXTRACTOR_PACKAGING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                AssetExtractorPackagingRegression.Run();
                Console.WriteLine("PASS: AssetExtractor packaging isolation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_PAUSE_TIMING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                PauseTimingStockRegression.Run();
                Console.WriteLine("PASS: stock Pause timing regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_INITIAL_HITSOUND_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                InitialHitSoundRegression.Run();
                Console.WriteLine("PASS: initial hitsound regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_STATIC_DECORATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                StaticDecorationRegression.Run();
                Console.WriteLine("PASS: static AddDecoration regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_MODEL_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                DecorationModelFoundationRegression.Run();
                Console.WriteLine("PASS: decoration model foundation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_EVENT_DELETE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EditorSessionEventDeletionRegression.Run();
                EventDeleteUiRegression.Run();
                Console.WriteLine("PASS: event deletion identity and UI regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_EVENT_CATEGORY_ICON_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EventCategoryIconRegression.Run();
                EventPaletteIconRegression.Run();
                EventAvailabilityRegression.Run();
                LegacyEventRoundTripRegression.Run();
                Console.WriteLine("PASS: event palette, availability, and legacy event round-trip regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_UI_CLEANUP_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                AssetSetupUiRegression.Run();
                EventCategoryIconRegression.Run();
                EventAvailabilityRegression.Run();
                EditorSessionEventDeletionRegression.Run();
                EventDeleteUiRegression.Run();
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
            GoToFloorRegression.Run();
            NativeOnlyViewportRegression.Run();
            EventCategoryIconRegression.Run();
            EventPaletteIconRegression.Run();
            EventAvailabilityRegression.Run();
            LegacyEventRoundTripRegression.Run();
            SaveNodeOwnershipRegression.Run();
            EditorSessionEventDeletionRegression.Run();
            EventDeleteUiRegression.Run();
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
            DecorationModelFoundationRegression.Run();
            StaticDecorationRegression.Run();
            PauseTimingStockRegression.Run();
            InitialHitSoundRegression.Run();
            AssetExtractorPackagingRegression.Run();
            TrackTransformScalingRegression.Run();
            VfxTimelineCoreRegression.Run();
            VfxTimelineExpansionRegression.Run();
            VfxTimelineEdgeRegression.Run();
            MoveDecorationsEvaluationRegression.Run();
            MoveDecorationsInterpolationRegression.Run();
            MoveDecorationsEasingRegression.Run();
            MoveDecorationsCoordinateRegression.Run();
            MoveDecorationsTilePlacementRegression.Run();
            MoveDecorationsRendererIntegrationRegression.Run();
            Console.WriteLine("PASS: legacy WinForms host removal, editor selection state, native-only production viewport, WPF asset setup/ADOFAI locator/setup UI/legacy import removal, floor geometry, icon bitmaps, level open/dirty-open guard, event palette/availability/legacy round-trip, decoration model foundation, playback setup, playback viewport, raster cache, raster streaming, raster scene rebase, raster worker, threading, playback diagnostics/layout/stall/lookahead, temporal playback rendering/routing/icon diagnostics/retention, native renderer ABI/host, runtime Tile camera reference, MoveTrack Tile camera freeze, Player camera reference conversion/smooth pivot, load preparation/progressive readiness, and performance regressions are valid.");
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
