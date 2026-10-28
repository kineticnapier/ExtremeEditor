using System.Diagnostics;
using System.Text.Json;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed record WpfLevelLoadResult(
    LevelDocument Document,
    SpatialGridIndex Index,
    LoadMetrics Metrics,
    TimeSpan IndexTime);

internal static class WpfLevelLoader
{
    public static WpfLevelLoadResult Load(string path) =>
        LoadAsync(path).GetAwaiter().GetResult();

    public static async Task<WpfLevelLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        LoadResult loaded;
        TrackVisualSourceBundle trackVisuals;
        TrackTransformSourceData trackTransforms;
        ScaleRadiusSourceData scaleRadius;
        CameraSourceData cameraSource;
        CameraSourceData cameraEvents;
        string loadPath = path;
        string? normalizedPath = null;
        if (LooseAdoFaiJson.HasUtf16Bom(path))
        {
            normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path);
            loadPath = normalizedPath;
        }

        try
        {
            try
            {
                (loaded, trackVisuals, trackTransforms, scaleRadius, cameraSource) =
                    await LoadPassesAsync(loadPath, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) when (normalizedPath is null)
            {
                // The game accepts literal control bytes inside strings. Normalize
                // the exceptional file once so every dedicated reader sees the same
                // UTF-8 representation as the core loader.
                normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path);
                loadPath = normalizedPath;
                (loaded, trackVisuals, trackTransforms, scaleRadius, cameraSource) =
                    await LoadPassesAsync(loadPath, cancellationToken).ConfigureAwait(false);
            }
            cameraEvents = AdoFaiCameraCompatibility.ToWorldUnits(cameraSource);
        }
        finally
        {
            if (normalizedPath is not null)
            {
                try { File.Delete(normalizedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        loaded.Document.SourcePath = path;

        loaded.Document.CameraSettings = new LevelCameraSettings(
            cameraSource.InitialRelativeTo,
            cameraSource.InitialPosition.X ?? 0.0,
            cameraSource.InitialPosition.Y ?? 0.0,
            cameraSource.InitialRotation,
            cameraSource.InitialZoom);

        // Narrow renderer passes stay out of the core model while still sharing
        // their parsed metadata across every native snapshot/timeline rebuild.
        TrackColorActionRecovery.MergeMissing(loaded.Document, trackVisuals.Legacy);
        TrackColorMetadataCache.Attach(loaded.Document, trackVisuals.Legacy);
        TrackVisualMetadataCache.Attach(loaded.Document, trackVisuals.Visual);
        TrackAnimationMetadataCache.Attach(loaded.Document, trackVisuals.Animation);
        TrackTransformMetadataCache.Attach(loaded.Document, trackTransforms);
        ScaleRadiusMetadataCache.Attach(loaded.Document, scaleRadius);
        CameraMetadataCache.Attach(loaded.Document, cameraEvents);

        // ScaleRadius changes floor-to-floor radius, so it has to run before the
        // spatial index and before PositionTrack/MoveTrack resolve against base
        // floor coordinates.
        ScaleRadiusResolver.ApplyGeometry(loaded.Document);

        var indexWatch = Stopwatch.StartNew();
        var index = new SpatialGridIndex(loaded.Document.Positions);
        indexWatch.Stop();

        return new WpfLevelLoadResult(
            loaded.Document,
            index,
            loaded.Metrics,
            indexWatch.Elapsed);
    }

    private static async Task<(LoadResult Loaded, TrackVisualSourceBundle Visuals,
        TrackTransformSourceData Transforms, ScaleRadiusSourceData ScaleRadius,
        CameraSourceData Camera)> LoadPassesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        LoadResult loaded = await AdoFaiLoader.LoadFlatAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return (
            loaded,
            TrackVisualSourceReader.Load(path, cancellationToken),
            TrackTransformSourceReader.Load(path, cancellationToken),
            ScaleRadiusSourceReader.Load(path, cancellationToken),
            CameraSourceReader.Load(path, cancellationToken));
    }
}
