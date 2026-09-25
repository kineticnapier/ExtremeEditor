# WPF Playback Raster Prefetch Implementation Plan

> **Historical snapshot:** This completed WPF comparison-renderer plan is
> retained for regression rationale. `LevelViewport` is not the production
> renderer; checklists, versions, and commands below are archival. See the
> repository README and `docs/README.md`.

**Goal:** Keep dense Follow Player playback at or above 30 FPS by removing dense raster generation from the WPF UI playback path and replacing the single synchronous raster with asynchronously generated, prefetched world-space chunks.

**Architecture:** `LevelViewport` will own a `RasterChunkCache` that tracks immutable chunk keys/results and queues work to a dedicated STA `RasterChunkWorker`. The worker owns its WPF rendering objects, produces frozen `BitmapSource` instances, and publishes them without blocking the UI thread; the viewport composes ready chunks plus vector icon/selection overlays. Playback only updates camera/pose, installs completed chunks, and enqueues deduplicated prefetch requests.

**Tech Stack:** .NET 8, WPF `DrawingVisual`/`RenderTargetBitmap`, `Dispatcher`, `Thread`, `ConcurrentQueue`, ExtremeEditor.Core spatial index, existing WPF regression executable.

**Spec:** `docs/superpowers/specs/2026-09-20-wpf-playback-raster-prefetch-design.md`

## Global Constraints

- Follow Player remains enabled and keeps planets/camera visually current.
- Acceptance target: sustained playback at **>=30 FPS** on the problematic dense chart in Release mode.
- Active playback UI thread must never call dense `RenderTargetBitmap.Render()` or wait for raster worker completion.
- Cache misses during playback must never synchronously rebuild dense floor raster.
- Stock ADOFAI floor appearance remains intact; no nearby-tile dots LOD.
- Event/floor icons remain visible under the existing `MinIconZoom` rule.
- Normal playback should show no intentional blank gaps or visible stale-scene lag.
- Level changes invalidate pending work by generation so stale results cannot install.
- Selection changes must not force dense floor raster regeneration.
- Existing logical scene ordering regressions remain green.
- Every implementation/code batch bumps `EditorVersion.Current`.
- Work stays on `feature/wpf-migration`.
- TDD order is RED -> user/local verification of intended failure -> GREEN -> user/local verification.

## Review Focus

- Rapid level replacement while old chunks are queued: stale generation must never publish into the new level.
- Zoom changes while worker jobs are outstanding: wrong-scale chunks must not replace current-scale chunks.
- Repeated requests for the same missing chunk: request coalescing must prevent unbounded queue growth.
- Worker shutdown/window close while jobs are queued: no deadlock, dispatcher hang, or post-disposal UI callback.
- Renderer geometry cache accessed from UI and worker threads: cache mutation must be thread-safe and all shared WPF resources must be frozen.

---

### Task 1: Add RED regressions for non-blocking playback raster behavior

**Files:**
- Create: `src/ExtremeEditor.Wpf.Tests/PlaybackRasterPrefetchRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Consumes: existing `LevelViewport.SetPlaybackPose(PlaybackPose?)`, `StaticSceneRasterCacheActive`, `StaticSceneRasterCacheBuildCount`.
- Produces test-facing diagnostics expected from later tasks:
  - `int PlaybackSynchronousRasterBuildCount { get; }`
  - `int RasterChunkRequestsQueued { get; }`
  - `int RasterChunkBuildsCompleted { get; }`
  - `int RasterVisibleMissingChunkCount { get; }`
  - `long RasterCacheGeneration { get; }`

- [ ] **Step 1: Write the failing regression executable test**

Create `PlaybackRasterPrefetchRegression.cs` with three initial RED checks: playback crossing cache coverage must not increment synchronous dense raster builds, a missing chunk must enqueue without waiting, and a level reset must change generation.

```csharp
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackRasterPrefetchRegression
{
    public static void Run()
    {
        VerifyPlaybackFollowDoesNotSynchronouslyBuildDenseRaster();
        VerifyMissingChunkRequestDoesNotBlockPlaybackUpdate();
        VerifyLevelResetAdvancesRasterGeneration();
    }

    private static void VerifyPlaybackFollowDoesNotSynchronouslyBuildDenseRaster()
    {
        LevelViewport viewport = CreateDenseViewport(out LevelDocument level);
        viewport.FollowPlayer = true;
        Render(viewport);

        int before = ReadInt(viewport, "PlaybackSynchronousRasterBuildCount");
        for (int i = 0; i < 24; i++)
        {
            Vector2 p = new(i * 1.5f, 0f);
            viewport.SetPlaybackPose(new PlaybackPose(i, 0.5, p, p + Vector2.UnitX, true, false));
        }

        int after = ReadInt(viewport, "PlaybackSynchronousRasterBuildCount");
        if (after != before)
            throw new InvalidOperationException($"Playback performed synchronous raster builds: before={before}, after={after}.");
    }

    private static void VerifyMissingChunkRequestDoesNotBlockPlaybackUpdate()
    {
        LevelViewport viewport = CreateDenseViewport(out _);
        viewport.FollowPlayer = true;
        Render(viewport);

        var watch = Stopwatch.StartNew();
        viewport.SetPlaybackPose(new PlaybackPose(0, 0.5, new Vector2(40f, 0f), new Vector2(41f, 0f), true, false));
        watch.Stop();

        if (ReadInt(viewport, "RasterChunkRequestsQueued") <= 0)
            throw new InvalidOperationException("Playback cache miss must enqueue raster work.");
        if (watch.ElapsedMilliseconds >= 33)
            throw new InvalidOperationException($"Playback update blocked for {watch.ElapsedMilliseconds} ms while raster was unavailable.");
    }

    private static void VerifyLevelResetAdvancesRasterGeneration()
    {
        LevelViewport viewport = CreateDenseViewport(out _);
        long before = ReadLong(viewport, "RasterCacheGeneration");
        LevelDocument replacement = CreateDenseLevel(5_000, 100f);
        viewport.SetLevel(replacement, new SpatialGridIndex(replacement.Positions));
        long after = ReadLong(viewport, "RasterCacheGeneration");
        if (after <= before)
            throw new InvalidOperationException($"Level reset must advance raster generation: before={before}, after={after}.");
    }

    private static LevelViewport CreateDenseViewport(out LevelDocument level)
    {
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        level = CreateDenseLevel(5_000, 0f);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        return viewport;
    }

    private static LevelDocument CreateDenseLevel(int count, float offsetX)
    {
        var positions = new Vector2[count];
        for (int i = 0; i < count; i++)
            positions[i] = new Vector2(offsetX + (i % 100) * 0.08f, (i / 100) * 0.08f);
        return new LevelDocument
        {
            SourcePath = "<playback-raster-prefetch-regression>",
            Angles = new double[Math.Max(0, count - 1)],
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
            InitialBpm = 120,
            PitchPercent = 100,
            CountdownTicks = 0,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = PathBuilder.CalculateBounds(positions)
        };
    }

    private static int ReadInt(LevelViewport viewport, string name) =>
        typeof(LevelViewport).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(viewport) is int value
            ? value
            : throw new InvalidOperationException($"LevelViewport.{name} is missing.");

    private static long ReadLong(LevelViewport viewport, string name) =>
        typeof(LevelViewport).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(viewport) is long value
            ? value
            : throw new InvalidOperationException($"LevelViewport.{name} is missing.");

    private static void Render(LevelViewport viewport)
    {
        MethodInfo onRender = typeof(LevelViewport).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.OnRender is missing.");
        var visual = new DrawingVisual();
        using DrawingContext dc = visual.RenderOpen();
        onRender.Invoke(viewport, [dc]);
    }
}
```

- [ ] **Step 2: Execute the new regression from `Program.cs`**

Add:

```csharp
PlaybackRasterPrefetchRegression.Run();
```

next to the existing WPF regression calls.

- [ ] **Step 3: Bump version for the RED batch**

Change `EditorVersion.Current` from `0.0.79-prototype` to `0.0.80-prototype`.

- [ ] **Step 4: Run to verify intended RED**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected first failure: `LevelViewport.PlaybackSynchronousRasterBuildCount is missing.` Do not implement production behavior until the user confirms this RED locally.

- [ ] **Step 5: Commit RED**

```bash
git add src/ExtremeEditor.Wpf.Tests/PlaybackRasterPrefetchRegression.cs src/ExtremeEditor.Wpf.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "test: require non-blocking playback raster cache"
```

---

### Task 2: Introduce chunk keys/results and a deterministic cache state machine

**Files:**
- Create: `src/ExtremeEditor.Wpf/RasterChunkCache.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/RasterChunkCacheRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Produces:
  - `internal readonly record struct RasterChunkKey(int X, int Y, int ZoomBucket, long Generation);`
  - `internal sealed record RasterChunkResult(RasterChunkKey Key, Rect ScreenRect, BitmapSource Bitmap);`
  - `RasterChunkCache.Reset(long generation)`
  - `bool RasterChunkCache.TryMarkQueued(RasterChunkKey key)`
  - `bool RasterChunkCache.TryPublish(RasterChunkResult result)`
  - `bool RasterChunkCache.TryGetReady(RasterChunkKey key, out RasterChunkResult? result)`
  - `IReadOnlyList<RasterChunkKey> RasterChunkCache.GetReadyKeys()`
- Later tasks depend on cache coalescing and stale-generation rejection being correct without WPF worker involvement.

- [ ] **Step 1: Write cache RED tests**

Create `RasterChunkCacheRegression.cs` checking duplicate queue suppression, publication, stale generation rejection, and reset invalidation.

```csharp
internal static class RasterChunkCacheRegression
{
    public static void Run()
    {
        var cache = new RasterChunkCache();
        cache.Reset(7);
        var key = new RasterChunkKey(1, 2, 28, 7);
        if (!cache.TryMarkQueued(key) || cache.TryMarkQueued(key))
            throw new InvalidOperationException("Raster chunk requests must be coalesced.");

        cache.Reset(8);
        var stale = new RasterChunkKey(1, 2, 28, 7);
        if (cache.TryMarkQueued(stale))
            throw new InvalidOperationException("Old-generation chunk requests must be rejected after reset.");
    }
}
```

Add `RasterChunkCacheRegression.Run();` to WPF tests.

- [ ] **Step 2: Run and verify RED**

Run the WPF tests. Expected failure is compile-time missing `RasterChunkCache`/`RasterChunkKey`.

- [ ] **Step 3: Implement minimal cache state machine**

Use dictionaries/sets owned by the UI thread; no locks are required if all cache mutation is UI-only. Reject any key whose `Generation != CurrentGeneration`. `TryPublish` must remove the key from pending and replace only the matching-generation ready entry.

- [ ] **Step 4: Add frozen-publication assertion**

Construct a 1x1 `WriteableBitmap`, freeze it, publish it, and assert `TryGetReady` returns the same frozen bitmap. Also attempt to publish a stale result after `Reset(9)` and assert rejection.

- [ ] **Step 5: Run all WPF regressions**

Expected: cache-specific tests pass; Task 1 playback RED still fails because no viewport integration exists yet.

- [ ] **Step 6: Bump version and commit**

Bump to `0.0.81-prototype` and commit:

```bash
git add src/ExtremeEditor.Wpf/RasterChunkCache.cs src/ExtremeEditor.Wpf.Tests/RasterChunkCacheRegression.cs src/ExtremeEditor.Wpf.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: add raster chunk cache state machine"
```

---

### Task 3: Make floor rendering safe for a dedicated STA worker

**Files:**
- Modify: `src/ExtremeEditor.Wpf/WpfFloorRenderer.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/FloorRendererThreadingRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Consumes: `WpfFloorRenderer.BeginFrame(float)` and `DrawFloor(...)`.
- Produces: independent renderer instances safe to create/use on separate STA threads, with shared cached `StreamGeometry` access synchronized and every cross-thread WPF resource frozen.

- [ ] **Step 1: Add RED for concurrent geometry acquisition**

Create a test that starts two STA threads, creates one `WpfFloorRenderer` per thread, draws several identical/different angle combinations into separate `DrawingVisual`s, captures exceptions, joins both threads, and fails on any exception.

- [ ] **Step 2: Run repeatedly to expose current static `Dictionary<GeometryKey, StreamGeometry>` race**

Run the WPF test executable in a loop (for example 20 launches). If the race does not reproduce deterministically, keep the test as a structural regression and inspect production code before GREEN; do not claim current `Dictionary` is safe.

- [ ] **Step 3: Replace unsynchronized static geometry cache mutation**

Use a lock around lookup/build/add while continuing to freeze geometries before publication:

```csharp
private static readonly object GeometryCacheGate = new();

private static StreamGeometry GetCachedGeometry(float entryAngle, float exitAngle, bool midSpin)
{
    var key = ...;
    lock (GeometryCacheGate)
    {
        if (GeometryCache.TryGetValue(key, out StreamGeometry? cached))
            return cached;
        StreamGeometry geometry = BuildGeometry(...);
        geometry.Freeze();
        GeometryCache.Add(key, geometry);
        return geometry;
    }
}
```

Do not share mutable `Pen` instances between renderer instances; each worker renderer keeps its own per-instance pens. Existing static brushes/geometries remain frozen.

- [ ] **Step 4: Run WPF regressions and threading loop**

Expected: no threading exceptions; existing stock appearance tests remain green.

- [ ] **Step 5: Bump version and commit**

Bump to `0.0.82-prototype` and commit `fix: make WPF floor geometry cache thread-safe`.

---

### Task 4: Add the dedicated STA raster chunk worker

**Files:**
- Create: `src/ExtremeEditor.Wpf/RasterChunkWorker.cs`
- Create: `src/ExtremeEditor.Wpf/RasterChunkTypes.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/RasterChunkWorkerRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Produces:
  - `RasterChunkRequest` containing key, immutable world rect, pixel dimensions, zoom, candidate floor indices, level positions/angles references for the same generation.
  - `RasterChunkWorker.Enqueue(RasterChunkRequest request)` — returns immediately.
  - `bool RasterChunkWorker.TryDequeueCompleted(out RasterChunkResult? result)` — non-blocking.
  - `int RequestedCount`, `int CompletedCount` diagnostics.
  - `Dispose()` that stops the STA dispatcher without waiting on UI callbacks.

- [ ] **Step 1: Add RED that worker produces a frozen bitmap off the caller thread**

The test records caller managed thread id, enqueues a tiny request, pumps/waits with a bounded test-only timeout outside any playback code, dequeues result, and asserts:

```csharp
if (!result.Bitmap.IsFrozen)
    throw new InvalidOperationException("Worker bitmap must be frozen before publication.");
if (worker.LastBuildThreadId == Environment.CurrentManagedThreadId)
    throw new InvalidOperationException("Raster worker rendered on the caller/UI thread.");
```

Also enqueue a second request and dispose immediately; test must terminate cleanly.

- [ ] **Step 2: Run and verify RED**

Expected compile failure because worker/types do not exist.

- [ ] **Step 3: Implement worker thread lifecycle**

Create a background `Thread`, call `SetApartmentState(ApartmentState.STA)` before `Start()`, initialize a `Dispatcher` on that thread, own a worker-local `WpfFloorRenderer`, and process queued requests on that dispatcher. Completed results go into `ConcurrentQueue<RasterChunkResult>`.

The UI side must only call `BeginInvoke`/queue semantics; never `Dispatcher.Invoke`.

- [ ] **Step 4: Implement chunk rasterization**

For each request:

```csharp
var visual = new DrawingVisual();
using (DrawingContext dc = visual.RenderOpen())
{
    renderer.BeginFrame(request.Zoom);
    foreach (int floor in request.CandidateFloorsDescending)
        DrawRequestedFloor(dc, request, floor);
}
var bitmap = new RenderTargetBitmap(request.PixelWidth, request.PixelHeight, 96, 96, PixelFormats.Pbgra32);
bitmap.Render(visual);
bitmap.Freeze();
_completed.Enqueue(new RasterChunkResult(request.Key, request.ScreenRect, bitmap));
```

The request coordinate transform must be self-contained; it must not access `LevelViewport`, `_camera`, `_renderCameraOverride`, or any UI-thread visual.

- [ ] **Step 5: Run worker + full WPF regressions**

Expected: worker regression green; Task 1 viewport regression still RED.

- [ ] **Step 6: Bump version and commit**

Bump to `0.0.83-prototype`; commit `feat: add STA raster chunk worker`.

---

### Task 5: Integrate chunk composition into dense static scene without playback blocking

**Files:**
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Scene.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Rendering.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Playback.cs`
- Create: `src/ExtremeEditor.Wpf/LevelViewport.RasterChunks.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/PlaybackRasterPrefetchRegression.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Consumes: `RasterChunkCache`, `RasterChunkWorker`.
- Produces viewport diagnostics required by Task 1.
- Playback behavior: `SetPlaybackPose` may update transforms, drain completed results, and enqueue requests, but must not call the old synchronous dense `DrawRasterCachedMeshPreview` path.

- [ ] **Step 1: Split chunk orchestration into `LevelViewport.RasterChunks.cs`**

Add fields for worker/cache/generation and methods with exact responsibilities:

```csharp
private readonly RasterChunkCache _rasterChunks = new();
private readonly RasterChunkWorker _rasterWorker = new();
private long _rasterGeneration;

private void ResetRasterChunks();
private void UpdateRasterChunksForViewport(bool playbackActive);
private void DrainCompletedRasterChunks();
private void QueueVisibleAndPrefetchChunks(WorldRect viewport, bool playbackActive);
private void RebuildRasterChunkVisuals();
```

Expose diagnostics as read-only properties on `LevelViewport`.

- [ ] **Step 2: Change dense scene selection to chunk mode**

When candidate density exceeds `DenseRasterCandidateThreshold`, mark raster mode active but do not call `RenderTargetBitmap.Render()` from `BuildStaticScene`. Instead retain a lightweight composition visual and queue chunks. Non-dense scenes keep the existing vector path.

Keep `StaticSceneLayerCount == 1` logically by placing chunk image drawings and icon overlay in the single `_sceneVisual`/single retained scene root used by the existing regression.

- [ ] **Step 3: Make active playback explicitly non-blocking**

In `SetPlaybackPose`, after moving `_camera`, replace the synchronous dense coverage rebuild behavior with:

```csharp
DrainCompletedRasterChunks();
UpdateStaticSceneTransform();
UpdateRasterChunksForViewport(playbackActive: true);
RenderPlaybackVisual();
```

If a visible chunk is missing, increment `RasterVisibleMissingChunkCount`, queue it at highest priority, and continue. Never call the old dense synchronous raster method from this branch.

Increment `PlaybackSynchronousRasterBuildCount` only around any legacy synchronous dense raster method so the regression proves it stays unchanged during follow playback.

- [ ] **Step 4: Keep non-playback/manual behavior functional**

Manual pan/zoom may use the same async chunk path. Zoom changes increment generation/zoom bucket and queue replacements; old ready visuals remain until matching replacements arrive.

- [ ] **Step 5: Run Task 1 regression**

Expected: `PlaybackSynchronousRasterBuildCount` remains unchanged; requests are queued; level reset advances generation. If the wall-clock `33 ms` test is flaky on a loaded machine, retain it only as a coarse smoke assertion and rely on manual FPS acceptance for performance.

- [ ] **Step 6: Run all WPF regressions**

Verify existing raster cache active/build count semantics are adapted so `StaticSceneRasterCacheBuildCount` means completed chunk raster builds rather than one monolithic bitmap, while the existing dense-raster regression still observes `> 0` after its test pumps completion.

- [ ] **Step 7: Bump version and commit**

Bump to `0.0.84-prototype`; commit `feat: compose dense WPF scene from async raster chunks`.

---

### Task 6: Add prefetch prioritization and request coalescing for Follow Player

**Files:**
- Modify: `src/ExtremeEditor.Wpf/RasterChunkWorker.cs`
- Modify: `src/ExtremeEditor.Wpf/RasterChunkCache.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.RasterChunks.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/PlaybackRasterPrefetchRegression.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Produces priority classes: visible = 0, forward lookahead = 1, safety margin = 2.
- First implementation uses bounded fixed lookahead of 3 viewport widths in current camera-motion direction.

- [ ] **Step 1: Add RED for duplicate coalescing under repeated playback updates**

Call `SetPlaybackPose` repeatedly without advancing far enough to change required chunk set. Assert `RasterChunkRequestsQueued` grows only when a new chunk key is introduced, not once per frame.

- [ ] **Step 2: Add RED for forward prefetch**

Move camera monotonically in +X over several poses and expose/test `RasterPrefetchReadyOrQueuedCount`; assert chunks beyond the current visible rectangle are queued before the camera reaches them.

- [ ] **Step 3: Implement prioritized request queue**

Use a worker-owned priority structure or three queues. Deduplicate at `RasterChunkCache.TryMarkQueued` before enqueue. Process visible queue first, then lookahead, then safety margin.

- [ ] **Step 4: Implement fixed lookahead**

Track previous playback camera. If delta magnitude exceeds a small epsilon, normalize it and extend prefetch region by 3 viewport widths in that direction. Keep one viewport width around all sides as safety margin.

Do not add velocity-adaptive logic unless manual measurement shows fixed lookahead misses at normal playback; YAGNI.

- [ ] **Step 5: Run all WPF regressions**

Expected: no duplicate queue growth, ahead chunks queued, existing icon/raster/order tests green.

- [ ] **Step 6: Bump version and commit**

Bump to `0.0.85-prototype`; commit `perf: prefetch dense WPF raster chunks during playback`.

---

### Task 7: Move selection out of floor raster and preserve icon overlay behavior

**Files:**
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Rendering.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.RasterChunks.cs`
- Modify: `src/ExtremeEditor.Wpf/WpfFloorRenderer.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/PerformanceRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/DenseIconRegression.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Raster worker always draws floors with `selected: false`.
- UI overlay draws selected-floor outline independently.
- Existing `DrawMeshPreviewIcons` remains vector/retained and action-floor-only.

- [ ] **Step 1: Add RED that changing selection does not increment raster build request count**

Render a dense viewport, capture `RasterChunkRequestsQueued`, change selected floor through the existing selection input/helper, render again, and assert no new floor raster request is issued solely due to selection.

- [ ] **Step 2: Add a selected-floor overlay draw method**

Reuse the same geometry/angles but draw only the selection outline in a lightweight UI-owned visual. Do not bake selected state into chunk bitmap requests.

- [ ] **Step 3: Preserve icons above raster chunks**

Keep icons in the same logical scene layer after chunk images and before/with selection overlay as required for appearance. `StaticSceneIconsEnabled` remains based on zoom, never candidate count.

- [ ] **Step 4: Run dense icon, ordering, raster, and selection regressions**

Expected: all green; selection produces no chunk rebuild request.

- [ ] **Step 5: Bump version and commit**

Bump to `0.0.86-prototype`; commit `perf: keep selection out of dense raster chunks`.

---

### Task 8: Handle stale zoom/level work and clean shutdown

**Files:**
- Modify: `src/ExtremeEditor.Wpf/RasterChunkWorker.cs`
- Modify: `src/ExtremeEditor.Wpf/RasterChunkCache.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.RasterChunks.cs`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/RasterChunkWorkerRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/RasterChunkCacheRegression.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- `RasterChunkWorker.Dispose()` is idempotent.
- Generation and zoom bucket are part of every key; publication requires exact current key generation/bucket.

- [ ] **Step 1: Add RED for stale result rejection after level and zoom changes**

Queue a result for generation N/zoom bucket Z, reset to N+1 or Z+1, then attempt publication and assert `TryPublish` returns false and ready visual count is unchanged.

- [ ] **Step 2: Add RED for disposal with pending jobs**

Queue multiple synthetic worker requests, call `Dispose()` twice, and assert test process exits without blocking. No completed callback may touch disposed UI state.

- [ ] **Step 3: Implement cancellation-by-generation**

Worker may finish stale work, but cache publication rejects it. Before expensive draw begins, worker may cheaply discard requests whose generation is marked obsolete; correctness must not depend on cancellation winning a race.

- [ ] **Step 4: Dispose viewport worker on window shutdown**

Add a viewport disposal/shutdown method or make the worker lifetime owned by `MainWindow`; invoke it from `OnClosed` before base close. Do not synchronously wait for arbitrary raster work to finish; request dispatcher shutdown and join only with a short deterministic worker-exit path.

- [ ] **Step 5: Run full WPF suite repeatedly**

Run at least 20 launches to catch shutdown/threading races.

- [ ] **Step 6: Bump version and commit**

Bump to `0.0.87-prototype`; commit `fix: reject stale raster chunks and stop worker cleanly`.

---

### Task 9: Add playback diagnostics and perform manual >=30 FPS acceptance

**Files:**
- Modify: `src/ExtremeEditor.Wpf/MainWindow.AudioDiagnostics.cs`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.RasterChunks.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Diagnostics expose rolling/max UI playback update duration and chunk requested/completed/missing counters without per-frame log spam.

- [ ] **Step 1: Add lightweight playback timing instrumentation**

Wrap `UpdatePlaybackDisplay()` with `Stopwatch.GetTimestamp()` and maintain rolling/max values using primitive fields. Do not allocate collections or strings except for the existing visible status text update.

- [ ] **Step 2: Surface diagnostic snapshot on demand/existing diagnostics path**

Include:

```text
playback-ui max=<ms> chunks req=<n> done=<n> missing=<n> syncDense=<n>
```

Do not emit a log line every frame.

- [ ] **Step 3: Run automated suites**

```powershell
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
dotnet run -c Release --project src\ExtremeEditor.Audio.Tests
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: all pass.

- [ ] **Step 4: Manual acceptance on the problematic large chart**

Run Release WPF editor, load the same dense chart that previously hitched, enable Follow Player and Hitsounds, replay the previously problematic dense section for at least 30 seconds, and verify:

```text
sustained FPS >= 30
no periodic hitch correlated with cache boundary crossing
PlaybackSynchronousRasterBuildCount == 0 during playback
visible missing chunks normally == 0 after warmup
icons remain visible at zoom >= MinIconZoom
no blank floor gaps during normal playback
```

If FPS drops below 30 while `syncDense == 0`, capture max playback UI duration plus chunk missing/build durations before changing architecture. First tune chunk pixel dimensions/lookahead; add velocity-adaptive prefetch only if evidence shows fixed lookahead cannot stay ahead.

- [ ] **Step 5: Bump version and commit diagnostics**

Bump to `0.0.88-prototype`; commit `perf: add WPF playback raster diagnostics`.

- [ ] **Step 6: Final verification before completion**

Record the user's local results for all three test executables and the manual >=30 FPS playback acceptance. Do not claim completion until those results are green.
