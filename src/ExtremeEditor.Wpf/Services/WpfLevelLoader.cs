using System.Diagnostics;
using System.Text.Json;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed record WpfLevelLoadResult(
    LevelDocument Document,
    SpatialGridIndex Index,
    LoadMetrics Metrics,
    TimeSpan IndexTime)
{
    // Missing commas the loader supplied, empty for a strictly valid chart.
    internal IReadOnlyList<JsonRepair> JsonRepairs { get; init; } = [];
}

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
        IReadOnlyList<JsonRepair> repairs = [];
        if (LooseAdoFaiJson.HasUtf16Bom(path))
        {
            normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path, out repairs);
            loadPath = normalizedPath;
        }

        // Native preparation reads the background settings right after load.
        // Warm that cache alongside parsing; a failure here is reported by
        // the later read that actually needs the value.
        Task backgroundPreload = Task.Run(() =>
        {
            try { BackgroundSourceReader.ReadInitial(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }, CancellationToken.None);

        try
        {
            try
            {
                (loaded, trackVisuals, trackTransforms, scaleRadius, cameraSource) =
                    await LoadPassesAsync(loadPath, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) when (normalizedPath is null)
            {
                // The game accepts literal control bytes inside strings and
                // missing commas between values. Normalize the exceptional file
                // once so every dedicated reader sees the same UTF-8 text as the
                // core loader. The strictly valid path above stays untouched.
                normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path, out repairs);
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
        if (repairs.Count > 0)
        {
            Console.WriteLine(
                $"[load] json-repair inserted {repairs.Count} missing comma(s) after " +
                string.Join(", ", repairs.Take(8).Select(static r => $"line {r.Line} col {r.Column}")) +
                (repairs.Count > 8 ? ", ..." : string.Empty));
        }

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
        await backgroundPreload.ConfigureAwait(false);

        return new WpfLevelLoadResult(
            loaded.Document,
            index,
            loaded.Metrics,
            indexWatch.Elapsed)
        {
            JsonRepairs = repairs
        };
    }

    private static async Task<(LoadResult Loaded, TrackVisualSourceBundle Visuals,
        TrackTransformSourceData Transforms, ScaleRadiusSourceData ScaleRadius,
        CameraSourceData Camera)> LoadPassesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // The narrow renderer passes are independent, read-only streaming scans
        // of the same file and touch no shared document state, so they run
        // alongside the core loader instead of after it.
        Task<TrackVisualSourceBundle> visuals = Task.Run(
            () => TrackVisualSourceReader.Load(path, cancellationToken), CancellationToken.None);
        Task<TrackTransformSourceData> transforms = Task.Run(
            () => TrackTransformSourceReader.Load(path, cancellationToken), CancellationToken.None);
        Task<ScaleRadiusSourceData> scaleRadius = Task.Run(
            () => ScaleRadiusSourceReader.Load(path, cancellationToken), CancellationToken.None);
        Task<CameraSourceData> camera = Task.Run(
            () => CameraSourceReader.Load(path, cancellationToken), CancellationToken.None);
        Task<LoadResult> core = AdoFaiLoader.LoadFlatAsync(path, cancellationToken);

        // Wait for every pass before reporting, so no scan still holds the file
        // when a caller retries with a normalized copy. Rethrow the first
        // failure in the original sequential order.
        Task[] passes = [core, visuals, transforms, scaleRadius, camera];
        try
        {
            await Task.WhenAll(passes).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task pass in passes)
                _ = pass.Exception; // mark every failure observed
            foreach (Task pass in passes)
            {
                if (pass.IsFaulted || pass.IsCanceled)
                    await pass.ConfigureAwait(false);
            }
            throw;
        }
        return (core.Result, visuals.Result, transforms.Result, scaleRadius.Result, camera.Result);
    }
}
