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
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_NUMERIC_INPUT_ONLY"),
                "1", StringComparison.Ordinal))
            {
                NumericInputSafetyRegression.Run();
                Console.WriteLine("PASS: finite numeric input and settings precision regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SAVE_RESERVATION_ONLY"),
                "1", StringComparison.Ordinal))
            {
                SavePathReservationRegression.Run();
                Console.WriteLine("PASS: cross-tab save destinations and async save UI ownership are safe.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_STRUCTURE_REFERENCE_ONLY"),
                "1", StringComparison.Ordinal))
            {
                StructureReferenceGenerationRegression.Run();
                Console.WriteLine("PASS: save/undo, clipboard and Save As reference generations are stable.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_EDITOR_UI_STATE_ONLY"),
                "1", StringComparison.Ordinal))
            {
                EditorUiStateIssueRegression.Run();
                TabPlaybackPreferenceRegression.Run();
                UiSafetyIssueRegression.Run();
                ManagedGeometryEditRegression.Run();
                SaveStructureTransformPerformanceRegression.Run();
                Console.WriteLine("PASS: editor UI state and managed geometry issue regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_FAILURE_RECOVERY_ONLY"),
                "1", StringComparison.Ordinal))
            {
                AssetSetupRegression.Run();
                RasterChunkWorkerRegression.Run();
                DocumentTabSwitchCacheRegression.Run();
                Console.WriteLine("PASS: process, raster, and native preparation failure recovery regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_OPEN_ISSUE_DATA_INTEGRITY_ONLY"),
                "1", StringComparison.Ordinal))
            {
                OpenIssueDataIntegrityRegression.Run();
                Console.WriteLine("PASS: open-issue data-integrity regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_WINDOW_CLOSE_UNSAVED_ONLY"),
                "1", StringComparison.Ordinal))
            {
                WindowCloseUnsavedTabsRegression.Run();
                Console.WriteLine("PASS: window-close unsaved-tabs regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_ASYNC_SAVE_GENERATION_ONLY"),
                "1", StringComparison.Ordinal))
            {
                AsyncSaveGenerationRegression.Run();
                Console.WriteLine("PASS: async save generation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SAVE_AS_ASSETS_ONLY"),
                "1", StringComparison.Ordinal))
            {
                SaveAsAssetReferenceRegression.Run();
                Console.WriteLine("PASS: Save As asset reference regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SAVE_SOURCE_IDENTITY_ONLY"),
                "1", StringComparison.Ordinal))
            {
                SaveSourceIdentityRegression.Run();
                Console.WriteLine("PASS: save source identity regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_PLANET_RADIUS_POSITION_TRACK_ONLY"),
                "1", StringComparison.Ordinal))
            {
                PlanetRadiusPositionTrackRegression.Run();
                Console.WriteLine("PASS: Planet radius is independent of PositionTrack.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SET_FILTER_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                SetFilterRuntimeRegression.Run();
                Console.WriteLine("PASS: SetFilter runtime regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SCREEN_TILE_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                ScreenTileRuntimeRegression.Run();
                Console.WriteLine("PASS: ScreenTile runtime regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_BLOOM_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                BloomRuntimeRegression.Run();
                Console.WriteLine("PASS: Bloom runtime regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_FLASH_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                FlashRuntimeRegression.Run();
                Console.WriteLine("PASS: Flash runtime regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_HEADLESS_CAMERA_FRAMING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                HeadlessCameraFramingRegression.Run();
                Console.WriteLine("PASS: Headless camera framing regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_STREAMING_ENCODER_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportStreamingEncoderRegression.Run();
                Console.WriteLine("PASS: Video Export streaming encoder regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_TRACK_PARITY_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportTrackParityRegression.Run();
                Console.WriteLine("PASS: Video Export track parity regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_ORCHESTRATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportOrchestrationRegression.Run();
                Console.WriteLine("PASS: Video Export VE5 orchestration regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_FFMPEG_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportFfmpegEncoderRegression.Run();
                Console.WriteLine("PASS: Video Export ffmpeg encoder regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_ENCODER_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportEncoderRegression.Run();
                Console.WriteLine("PASS: Video Export VE4 encoder regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_OFFLINE_AUDIO_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportOfflineAudioRegression.Run();
                Console.WriteLine("PASS: Video Export VE3 offline audio regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_FRAME_PIPELINE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportFramePipelineRegression.Run();
                Console.WriteLine("PASS: Video Export VE2 frame pipeline regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_VIDEO_EXPORT_FOUNDATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                VideoExportFoundationRegression.Run();
                Console.WriteLine("PASS: Video Export foundation regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CUSTOM_BACKGROUND_RENDERING_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CustomBackgroundRenderingRegression.Run();
                Console.WriteLine("PASS: CustomBackground rendering regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CUSTOM_BACKGROUND_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CustomBackgroundRegression.Run();
                Console.WriteLine("PASS: CustomBackground regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CLEAN_PLAY_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CleanPlayRegression.Run();
                Console.WriteLine("PASS: Clean Play regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MISC_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                MiscSettingsSliceRegression.Run();
                Console.WriteLine("PASS: Misc Settings slice regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEPARATE_COUNTDOWN_DEFAULT_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                SeparateCountdownDefaultRegression.Run();
                Console.WriteLine("PASS: separateCountdownTime stock-default regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_LEVEL_METADATA_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                LevelMetadataSettingsSliceRegression.Run();
                Console.WriteLine("PASS: Level Metadata Settings slice regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CAMERA_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CameraSettingsSliceRegression.Run();
                Console.WriteLine("PASS: Camera Settings slice regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TRACK_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TrackSettingsSliceRegression.Run();
                Console.WriteLine("PASS: Track Settings slice regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TRACK_ANIMATION_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TrackAnimationSettingsRegression.Run();
                Console.WriteLine("PASS: root Track Animation Settings regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SELECTION_VISUALIZATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EditorSelectionVisualizationRegression.Run();
                Console.WriteLine("PASS: selection visualization regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CLIPBOARD_SAFETY_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EditorClipboardSafetyRegression.Run();
                Console.WriteLine("PASS: clipboard safety regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MIDSPIN_REVERSE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                EditorMidspinReverseRegression.Run();
                Console.WriteLine("PASS: Midspin reverse-input regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SONG_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                SongSettingsSliceRegression.Run();
                Console.WriteLine("PASS: Song Settings slice regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_MULTI_TAB_OPEN_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                OpenDirtyDocumentRegression.Run();
                Console.WriteLine("PASS: opening ADOFAI files preserves existing tabs and edits.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_SHUTDOWN_DIAGNOSTICS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                RenderThreadShutdownDiagnosticsRegression.Run();
                Console.WriteLine("PASS: post-D2D render thread shutdown phases are observable.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TAB_CYCLE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                DocumentTabNavigationRegression.Run();
                Console.WriteLine("PASS: cycling document tabs with Ctrl+Tab and Ctrl+Shift+Tab.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TAB_CLOSE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                DocumentTabCloseRegression.Run();
                DocumentTabSwitchCacheRegression.Run();
                NativeSceneReuseRegression.Run();
                Console.WriteLine("PASS: safe tab closing and cached managed/native scene switching.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DOCUMENT_TAB_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                DocumentTabScaffoldRegression.Run();
                Console.WriteLine("PASS: initial document-tab scaffold and dirty-state regression.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_EDITOR_OPERATIONS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TrackPositionEditorOnlyRegression.Run();
                EditorSelectionStateRegression.Run();
                EditorFloorClipboardRegression.Run();
                Console.WriteLine("PASS: editor operations regressions are valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_POSITION_TRACK_DRAG_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                PositionTrackDragRegression.Run();
                Console.WriteLine("PASS: Alt-drag PositionTrack regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_TWIRL_TOGGLE_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                TwirlToggleRegression.Run();
                Console.WriteLine("PASS: Twirl toggle regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_PLAYBACK_SPEED_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                PlaybackSpeedRegression.Run();
                PlaybackSpeedHitSoundRegression.Run();
                Console.WriteLine("PASS: playback speed regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_LOOSE_SOURCE_ROOT_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                LooseSourceRootRegression.Run();
                Console.WriteLine("PASS: loose ADOFAI source-root regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CAMERA_MIDSPIN_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CameraMidspinFollowRegression.Run();
                Console.WriteLine("PASS: Midspin follow-camera regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_ANIMATE_TRACK_RUNTIME_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                AnimateTrackRuntimeSemanticsRegression.Run();
                Console.WriteLine("PASS: AnimateTrack runtime semantics regression is valid.");
                return 0;
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_NATIVE_ICON_TRACK_ANIMATION_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                NativeIconTrackAnimationRegression.Run();
                Console.WriteLine("PASS: native icon Track Animation regression is valid.");
                return 0;
            }

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

            if (string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CAMERA_RUNTIME_SEMANTICS_ONLY"),
                "1",
                StringComparison.Ordinal))
            {
                CameraRuntimeSemanticsRegression.Run();
                Console.WriteLine("PASS: camera runtime semantics regression is valid.");
                return 0;
            }

            WinFormsHostRemovalRegression.Run();
            TrackPositionEditorOnlyRegression.Run();
            EditorFloorClipboardRegression.Run();
            PositionTrackDragRegression.Run();
            TwirlToggleRegression.Run();
            EditorSelectionStateRegression.Run();
            GoToFloorRegression.Run();
            NativeOnlyViewportRegression.Run();
            EventCategoryIconRegression.Run();
            EventPaletteIconRegression.Run();
            EventAvailabilityRegression.Run();
            LegacyEventRoundTripRegression.Run();
            SaveNodeOwnershipRegression.Run();
            SaveSourceIdentityRegression.Run();
            SaveAsAssetReferenceRegression.Run();
            AsyncSaveGenerationRegression.Run();
            WindowCloseUnsavedTabsRegression.Run();
            OpenIssueDataIntegrityRegression.Run();
            EditorUiStateIssueRegression.Run();
            TabPlaybackPreferenceRegression.Run();
            UiSafetyIssueRegression.Run();
            NumericInputSafetyRegression.Run();
            SavePathReservationRegression.Run();
            StructureReferenceGenerationRegression.Run();
            SaveStructureTransformPerformanceRegression.Run();
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
            DocumentTabCloseRegression.Run();
            DocumentTabSwitchCacheRegression.Run();
            NativeSceneReuseRegression.Run();
            DocumentTabNavigationRegression.Run();
            RenderThreadShutdownDiagnosticsRegression.Run();
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
