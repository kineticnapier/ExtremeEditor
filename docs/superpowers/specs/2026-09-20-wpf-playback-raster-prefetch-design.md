# WPF Playback Raster Prefetch Design

> **Historical snapshot:** This document describes the retained WPF comparison
> and regression renderer, not the production viewport. Production uses
> `NativeLevelViewport`. See the repository README and `docs/README.md`.

## Goal

Keep Follow Player playback visually responsive on dense charts without allowing static-scene raster work to block the WPF UI thread.

The user-visible target is **at least 30 FPS during playback**, including dense stock-tile sections where the current implementation can synchronously rebuild a `RenderTargetBitmap` when the camera exits the retained scene coverage.

## Current problem

`LevelViewport.SetPlaybackPose` moves the camera when Follow Player is enabled and calls `EnsureSceneCoverage()`. Dense scenes use a raster cache, but `BuildStaticScene()` currently performs floor drawing plus `RenderTargetBitmap.Render()` synchronously on the UI thread.

When playback camera motion crosses the cached coverage boundary, that synchronous rebuild creates a visible hitch. The same section plays smoothly with Follow Player disabled, which strongly isolates the problem to camera-follow scene cache maintenance rather than pose lookup or audio playback.

## Requirements

- Follow Player remains enabled and keeps the planets/camera visually current.
- Playback should maintain at least 30 FPS under expected dense-scene load.
- The playback UI thread must not perform expensive raster-cache generation.
- Stock ADOFAI floor appearance must remain intact; no dots LOD for nearby tiles.
- Event/floor icons remain visible under the existing zoom rule.
- No intentional blank gaps at normal playback speed.
- No normal-case stale-scene delay visible to the user.
- Cache misses must never fall back to a synchronous dense raster rebuild during playback.
- Existing one-layer logical scene ordering and selection behavior should be preserved where practical.

## Design

### 1. Chunked static raster cache

Replace the single dense-scene raster image with fixed world-space raster chunks.

Each chunk owns:

- a world-space rectangle,
- a frozen `BitmapSource` containing stock floor rendering for that rectangle,
- generation metadata (zoom bucket / level generation / selection generation as needed),
- readiness state.

Chunk dimensions should be chosen so one chunk is bounded in pixel size at the active zoom. The implementation should prefer multiple moderate bitmaps over one viewport-plus-margin bitmap whose regeneration cost can spike.

The UI scene is composed from ready chunks positioned by transforms. Icons remain a separate lightweight retained/vector overlay generated only for action-bearing floors in the relevant visible/near-visible region.

### 2. Dedicated STA raster worker

Raster generation runs on a dedicated background STA thread with its own `Dispatcher`.

The worker performs the WPF-affine work needed to build each chunk:

1. create temporary `DrawingVisual`,
2. draw stock floors intersecting the chunk,
3. render to `RenderTargetBitmap`,
4. `Freeze()` the resulting bitmap,
5. publish the immutable result back to the UI thread.

The UI thread only installs already-frozen bitmaps into retained visuals. It never calls `RenderTargetBitmap.Render()` for dense playback cache maintenance.

The worker receives immutable snapshots/references required for floor rendering. If current renderer state cannot safely cross threads, the worker owns its own `WpfFloorRenderer` instance and reads only immutable level data.

### 3. Playback-aware prefetch

Follow Player gives a predictable camera path. On every playback update the cache manager determines:

- chunks currently visible,
- chunks immediately surrounding the viewport,
- chunks ahead of current camera motion.

Requests are prioritized:

1. currently visible missing chunks,
2. next chunks along playback motion,
3. nearby safety-margin chunks.

Prefetch distance is adaptive. It should cover multiple future frames and may increase with camera/world velocity. The first implementation can use a bounded fixed lookahead (for example 2–4 viewport widths) if that satisfies the frame target; velocity-aware lookahead can follow only if measurements require it.

Duplicate requests are coalesced.

### 4. Double-buffered publication

A chunk becomes visible only after its replacement bitmap is fully built and frozen.

For any chunk that already has an older valid bitmap, keep displaying the old bitmap until the new version is ready, then atomically replace the image reference on the UI thread.

This prevents partial rendering or visible construction.

For a truly missing chunk, the cache manager should prioritize it before entry. Normal playback should avoid reaching this state through lookahead. If it does occur, the UI thread still must not synchronously build it.

### 5. Playback path must be non-blocking

During active playback, these operations are allowed on the UI thread:

- read current audio position,
- binary-search playback timing,
- compute playback pose,
- update camera transform,
- update planet retained visual,
- query chunk readiness,
- enqueue cheap/deduplicated worker requests,
- install completed frozen bitmaps,
- update small text/status elements.

These operations are forbidden from the active playback UI path:

- drawing thousands of floors,
- `RenderTargetBitmap.Render()`,
- rebuilding the full static scene,
- waiting on worker completion,
- synchronous dispatcher invocation onto the render worker,
- large allocations proportional to visible floor count.

### 6. Non-playback behavior

Manual pan/zoom does not need the same hard real-time guarantee, but should reuse the same chunk cache where practical.

Zoom invalidates raster chunks whose scale is no longer suitable. New zoom chunks are generated asynchronously. The previous valid scene may remain until replacements are ready rather than blocking interaction.

Level changes clear pending work through a generation/token mechanism so stale chunk results cannot be installed into a new chart.

Selection changes should not force floor raster regeneration if selection can remain as a separate overlay. If current floor renderer bakes selection into the static floor image, selection should be moved out of the raster layer as part of this change so selecting a floor cannot trigger dense raster rebuilds.

## Frame-budget and diagnostics

30 FPS gives a 33.3 ms frame budget. The design target is stronger: normal playback display updates should remain well below that budget so OS scheduling and rendering still have headroom.

Add lightweight diagnostics sufficient to measure:

- maximum/rolling playback UI update duration,
- raster chunk builds requested/completed,
- synchronous dense raster builds during playback,
- ready/missing visible chunk count,
- optional worker build duration.

No production path should spam logs every frame; counters/properties are enough for regressions and optional diagnostic display.

## TDD / regressions

Before production implementation, add failing regressions covering at least:

1. **No synchronous dense raster build during playback follow**
   - dense chart,
   - Follow Player enabled,
   - camera advances across enough distance to leave initial coverage,
   - synchronous/UI-thread raster build count must remain zero after playback starts.

2. **Playback updates do not wait for raster worker**
   - simulate an unavailable/not-ready chunk,
   - pose/camera update must complete without blocking for cache completion.

3. **Completed frozen chunk can be published**
   - worker result is immutable/frozen and accepted by the UI cache.

4. **Stale generation is ignored**
   - load/change level while older chunk work is pending,
   - old result must not install.

5. Existing dense stock-tile raster and dense icon regressions remain green.

Performance timing itself should be validated manually on the problematic large chart in Release mode because wall-clock thresholds in unit tests are environment-sensitive. The acceptance test is sustained playback at **>=30 FPS** with Follow Player enabled and no visible periodic hitch from ExtremeEditor cache rebuilds.

## Scope exclusions

This change does not attempt to optimize:

- `.adofai` file loading,
- TimingMap construction,
- audio decode/mixing beyond regressions needed to confirm playback remains unaffected,
- general WPF application startup,
- arbitrary GPU/driver/GC pauses outside ExtremeEditor-controlled synchronous rendering.

The goal is specifically to remove ExtremeEditor-controlled dense static raster generation from the playback UI critical path.
