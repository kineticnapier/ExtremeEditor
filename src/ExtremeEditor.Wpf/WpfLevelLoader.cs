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
        CameraSourceData cameraEvents;
        try
        {
            loaded = await AdoFaiLoader.LoadFlatAsync(path, cancellationToken)
                .ConfigureAwait(false);

            // The flat core loader intentionally keeps only timing/gameplay fields.
            // Read renderer-specific visual metadata in narrow streaming passes so
            // pathological charts never need a full actions DOM during load.
            trackVisuals = TrackVisualSourceReader.Load(path, cancellationToken);
            trackTransforms = TrackTransformSourceReader.Load(path, cancellationToken);
            cameraEvents = AdoFaiCameraCompatibility.ToWorldUnits(
                CameraSourceReader.Load(path, cancellationToken));
        }
        catch (JsonException)
        {
            // Some ADOFAI charts contain literal CR/LF/TAB bytes inside JSON strings.
            // The game accepts those loose files, while System.Text.Json correctly
            // rejects them. Keep the fast streaming path for normal charts and only
            // normalize the exceptional file into a temporary copy.
            string normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path);
            try
            {
                loaded = await AdoFaiLoader.LoadFlatAsync(normalizedPath, cancellationToken)
                    .ConfigureAwait(false);
                loaded.Document.SourcePath = path;
                trackVisuals = TrackVisualSourceReader.Load(normalizedPath, cancellationToken);
                trackTransforms = TrackTransformSourceReader.Load(normalizedPath, cancellationToken);
                cameraEvents = AdoFaiCameraCompatibility.ToWorldUnits(
                    CameraSourceReader.Load(normalizedPath, cancellationToken));
            }
            finally
            {
                try
                {
                    File.Delete(normalizedPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // Narrow renderer passes stay out of the core model while still sharing
        // their parsed metadata across every native snapshot/timeline rebuild.
        TrackColorActionRecovery.MergeMissing(loaded.Document, trackVisuals.Legacy);
        TrackColorMetadataCache.Attach(loaded.Document, trackVisuals.Legacy);
        TrackVisualMetadataCache.Attach(loaded.Document, trackVisuals.Visual);
        TrackTransformMetadataCache.Attach(loaded.Document, trackTransforms);
        CameraMetadataCache.Attach(loaded.Document, cameraEvents);

        var indexWatch = Stopwatch.StartNew();
        var index = new SpatialGridIndex(loaded.Document.Positions);
        indexWatch.Stop();

        return new WpfLevelLoadResult(
            loaded.Document,
            index,
            loaded.Metrics,
            indexWatch.Elapsed);
    }
}
