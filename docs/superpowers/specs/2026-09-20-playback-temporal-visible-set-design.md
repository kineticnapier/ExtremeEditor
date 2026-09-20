# Playback Temporal Visible-Set Renderer Design

## Goal

Replace dense-chart raster chunk rendering on the **active playback path** with a playback-time-aware direct WPF renderer inspired by the candidate-selection strategy observed in ADOPAC (A Dance of Python and C++).

The purpose is to make Follow Player playback robust on pathological charts such as the ~997,665-floor / 2.64e6 BPM test chart without depending on `RenderTargetBitmap` chunk lifetime, scene-anchor rebasing, stale bitmap publication, or window-resize resets.

The user-visible target remains:

- correct stock floor appearance,
- floor/event icons visible under the existing zoom rule,
- Follow Player enabled,
- no intentional stale/blank playback frames,
- sustained **>=30 FPS** on the problematic chart as the minimum acceptance threshold,
- continue toward ~60 FPS if the first direct-render implementation has sufficient headroom.

## Motivation

The current dense playback renderer has accumulated several classes of state that are difficult to keep coherent while the camera advances very quickly:

- asynchronous raster worker queues,
- ready/pending chunk sets,
- generation invalidation,
- world-to-scene chunk placement,
- retained `BitmapSource` lifetime,
- scene anchor and translate transforms,
- scene rebasing,
- icon coverage separate from floor coverage.

The bounded streaming fixes stopped unbounded bitmap retention and the WPF render-thread crash, but visual corruption can still persist until a window resize performs `ResetStaticScene()` / `ResetRasterChunks()` and rebuilds the scene from the current position.

That behavior is evidence that the raster-cache lifecycle itself remains a source of playback correctness risk.

ADOPAC takes a different approach: it keeps full-level timing/position data available, but per frame it narrows the floor set around the current playback time and then performs screen-space culling before drawing. The important idea is not its Python/C++ split; it is that a million-floor level does not imply scanning or drawing a million floors every frame.

ExtremeEditor already has the main primitives needed for the same strategy:

- `TimingMap` with sorted floor entry times,
- `TimingMap.FindFirstFloorAtOrAfter()` binary search,
- immutable level positions/angles during playback,
- `WpfFloorRenderer` cached/frozen floor geometry,
- `WpfIconRenderer` cached/frozen icon bitmaps,
- exact playback pose and Follow Player camera state.

## Scope

### In scope

- A playback-only temporal floor selector.
- A dedicated WPF playback floor/icon visual.
- Direct vector/image drawing of selected visible floors and icons each playback update.
- Playback diagnostics for temporal candidate and visible-draw counts.
- Dense playback routing away from raster chunk generation/composition.
- TDD regressions for selector complexity/correctness and renderer routing.
- Manual Release-mode validation on the known pathological chart.

### Out of scope

- Replacing the non-playback editor scene renderer.
- Removing raster chunk code entirely in this change.
- Rewriting `WpfFloorRenderer` geometry generation.
- Rewriting icon asset loading.
- Audio graph changes.
- `.adofai` loading or `TimingMap` construction optimization.
- Implementing ADOPAC's fixed `max_tile_show` truncation in the first version.

The existing static/raster renderer remains available for stopped editing and manual navigation. This design changes the active playback path only.

## Playback visibility semantics

This design intentionally changes **which floors belong to the active playback scene**.

Stopped editing remains spatial/static: floors that belong to the editor scene can remain visible regardless of playback time.

Active dense playback becomes time-local: only floors inside the configured playback-time window are eligible for drawing, followed by viewport culling. Floors outside the time window are intentionally absent until they enter it, and floors leave the playback visual after they exit the past window. This is the core ADOPAC-inspired optimization, not a stale-frame or quality fallback.

For floors that are eligible, rendering quality is unchanged: stock mesh shape, tile texture, and applicable icons are drawn at the normal quality. The first implementation does not additionally truncate the eligible set by a fixed floor-count cap.

Stopping playback restores the ordinary editor scene.

## Architecture

### 1. Playback temporal selector

Add a Core-side selector, tentatively `PlaybackVisibleFloorSelector`.

Inputs:

- `TimingMap`,
- `LevelDocument`,
- current chart time,
- past visibility window,
- future visibility window.

Output:

- an inclusive floor range or equivalent lightweight range descriptor,
- diagnostic counts describing how much of the timing sequence was considered.

The selector must never scan from floor 0 on every frame.

It should use the sorted timing data to locate both temporal boundaries with binary search:

```text
chartTime - pastWindow   -> first candidate floor
chartTime + futureWindow -> end candidate floor
```

`TimingMap.FindFirstFloorAtOrAfter()` already supplies the needed lower-bound behavior. If a companion upper-bound helper is clearer, it may be added to `TimingMap` rather than duplicating search logic in WPF.

The complexity target for selecting the candidate range is **O(log N)** plus work proportional to the returned temporal candidates, not O(N) in total floor count.

### 2. Temporal windows

The first implementation uses explicit internal past/future time windows rather than a fixed number of floors.

Reason: at 2.64e6 BPM, a fixed `currentFloor +/- K` policy has no stable relationship to playback time, while a time window remains meaningful across BPM changes.

The initial constants should be conservative enough that floors do not visibly pop in/out near the viewport during normal Follow Player playback. They must be centralized and testable, not scattered magic numbers.

The first version deliberately does **not** impose a hard `max_tile_show` floor-count truncation. ADOPAC uses such a bound, but ExtremeEditor should first measure whether temporal selection plus screen-space culling is sufficient while preserving the full time-local playback set.

If the pathological chart still exceeds the frame budget after this architecture is working correctly, a later change may introduce a measured budget/visibility policy with separate user approval.

### 3. Screen-space / viewport culling

Temporal selection is only the coarse first stage.

For each temporal candidate, WPF checks whether the floor can intersect the current viewport plus a small world-space margin large enough for the stock floor mesh/icon extent.

A candidate outside that expanded viewport is skipped before any floor geometry draw call.

The renderer therefore performs:

```text
TimingMap binary search
    -> temporal floor range
    -> position/viewport cull
    -> DrawFloor / DrawFloorIcon only for survivors
```

The margin must account for a floor whose center is just outside the viewport but whose mesh extends onto the screen. The test should cover this boundary condition so the cull itself cannot create clipped floors.

### 4. Playback state handoff

`WpfPlaybackPresenter` already computes chart time and playback pose from audio time. It becomes the bridge into the temporal renderer.

On each playback update it must provide the viewport with:

- the current `PlaybackPose`,
- current chart time,
- the current `TimingMap` reference (or an equivalent reference stored once before playback).

The exact method name is an implementation detail, but the contract must avoid recomputing chart time independently in multiple places. One authoritative chart-time calculation should feed both planet pose and temporal floor selection for the same frame.

When transport is stopped, the same handoff clears temporal playback state so the viewport can restore its normal static scene.

### 5. Playback-only retained visual

Add a dedicated retained visual, tentatively `_playbackFloorVisual`, between the normal static scene and the existing planet/selection overlay.

Logical ordering:

```text
_sceneRoot / static editor scene
_playbackFloorVisual / playback floors + icons
_playbackVisual / selection + planets
```

During active playback when the temporal renderer is enabled:

- dense floor raster chunks are not generated or composed for playback frames,
- the static dense floor layer is hidden/cleared as needed to avoid double drawing,
- `_playbackFloorVisual.RenderOpen()` redraws only the selected visible playback floors/icons,
- `_playbackVisual` continues to own planets and selection.

When playback stops:

- `_playbackFloorVisual` is cleared,
- the ordinary editor scene is restored/rebuilt through the existing stopped-editing path.

This keeps playback-state invalidation local. There is no playback floor bitmap cache, no scene-anchor transform, and no raster generation token involved in the new playback visual.

### 6. Floor rendering

The direct playback renderer reuses `WpfFloorRenderer` rather than creating a new appearance implementation.

For each surviving floor:

1. obtain its world position,
2. convert it with the current camera directly to screen coordinates,
3. obtain entry/exit angles using the existing floor-angle logic or a shared helper,
4. determine midspin state,
5. call `WpfFloorRenderer.DrawFloor(...)` with `selected: false`.

`WpfFloorRenderer` already caches frozen geometry by angular shape and reuses the imported stock tile brush. The playback renderer should preserve this behavior rather than rasterizing it again.

Selection remains on the existing lightweight overlay and must not force playback floor redraw semantics beyond the normal frame update.

### 7. Icon rendering

Icons are drawn in the same playback floor visual and the same current-camera coordinate system as floors.

For each surviving floor that has actions, reuse the existing icon decision logic (`SetFloorIcon`, checkpoint, twirl, speed icon, event fallback) and `WpfIconRenderer`.

The implementation should refactor/share the icon drawing helper if necessary rather than copy a second divergent icon policy.

This removes the current dense-raster failure mode where icon coverage can become detached from the camera/scene anchor.

### 8. Playback routing

The new renderer is intended to replace raster chunks for **active dense playback**, not for every editor state.

Routing rules:

- stopped/manual editing: current static scene path,
- active playback + dense mesh-preview scene: temporal direct playback renderer,
- active playback + non-dense scene: existing retained/static path may remain if it is already cheap and correct.

The exact dense threshold can initially reuse `DenseRasterCandidateThreshold` so this change does not alter the definition of a dense scene at the same time as changing rendering architecture.

During temporal-rendered playback, `PlaybackSynchronousRasterBuildCount` must remain zero and raster request counters should stop increasing because playback is no longer asking the chunk worker to maintain camera-follow coverage.

### 9. No intentional quality degradation inside the eligible set

The first implementation must not introduce:

- dots/overview LOD for eligible nearby playback floors,
- lower-resolution floor textures,
- stale-frame presentation,
- blank fallback frames,
- a fixed floor-count truncation after temporal selection.

The optimization is temporal scene membership plus viewport candidate elimination before draw, not quality reduction of floors that survive those two filters.

## Diagnostics

Extend playback diagnostics with at least:

- temporal candidate count,
- visible floor draw count,
- visible icon draw count,
- temporal selector lower/upper floor indices or selected range size,
- temporal playback floor-render duration,
- existing FPS / rolling UI milliseconds / max UI milliseconds.

Keep the current raster counters temporarily so manual testing can verify that active dense playback is no longer producing raster requests.

Example diagnostic intent:

```text
fps=... rolling=...ms max=...ms
temporal=420 visibleFloors=73 visibleIcons=9 draw=2.1ms
rasterReqDelta=0 syncDense=0
```

Diagnostics should use counters/properties and must not add per-floor logging.

## Correctness and performance requirements

### Selector requirements

- Correct lower/upper temporal boundaries for ordinary increasing entry times.
- Correct handling of equal-time floors such as midspins.
- Correct clamping before chart start and after chart end.
- Selection cost independent of floors far outside the time window.
- No allocation proportional to total level floor count per frame.

### Viewport-cull requirements

- A floor well outside the expanded viewport is not drawn.
- A floor center just outside the viewport but whose mesh may overlap the screen remains eligible.
- Camera movement does not depend on stale scene anchors.

### Playback renderer requirements

- Active dense playback uses the temporal playback visual rather than raster chunk composition.
- Floors and icons use the same current camera transform.
- Pose and temporal selection use the same chart time for a frame.
- Stopping playback restores the normal editor scene.
- No `RenderTargetBitmap.Render()` is introduced into the playback UI path.
- No background worker synchronization/wait is introduced into the playback UI path.

## TDD plan at design level

Implementation remains RED -> user verifies -> GREEN.

The first RED should establish the new architecture without depending on fragile wall-clock assertions:

1. **Temporal selector exists and does not require whole-level scan**
   - build a synthetic timing map large enough to expose accidental O(N) APIs,
   - verify a narrow time query returns a narrow range around the expected floor,
   - expose diagnostic inspected-count/search behavior if needed for a deterministic regression.

2. **Equal-time and boundary correctness**
   - verify lower/upper selection around equal entry times and chart boundaries.

3. **Playback state handoff and renderer routing**
   - presenter supplies one chart time to pose + temporal selection,
   - dense active playback must expose/use a temporal playback-render mode,
   - raster request count must not increase as Follow Player advances through multiple playback poses once temporal mode is active.

4. **Viewport culling preserves edge floors**
   - clearly outside candidate rejected,
   - candidate within mesh margin retained.

5. **Icons remain part of the temporal playback visual path**
   - action-bearing visible floor contributes icon drawing while an offscreen action-bearing floor does not.

6. Existing playback pose, floor geometry, icon cache, raster cache, worker, streaming, and diagnostics regressions remain green unless deliberately updated to account for the new playback routing.

Wall-clock FPS remains a manual Release-mode acceptance test, not a unit-test threshold.

## Manual acceptance

Primary test chart:

- ~997,665 floors,
- 2.64e6 BPM section,
- Follow Player ON,
- Hitsounds ON,
- stock floor preview ON,
- icons ON at applicable zoom.

Acceptance checks:

1. no window resize is needed to repair the scene,
2. no WPF render-thread crash,
3. no persistent missing/corrupted floor regions inside the intended time-local playback set,
4. icons remain aligned and visible for eligible floors,
5. raster request counters remain effectively unchanged during temporal-rendered dense playback,
6. `syncDense=0`,
7. sustained >=30 FPS on the known problematic section,
8. temporal/visible counts remain bounded by the local playback/view region rather than total level size.

If the renderer is correct but still below the performance target, the next optimization step should be chosen from measurements of temporal candidates, visible floors, WPF draw time, and frame cadence. A fixed floor-count cap or native renderer is not part of this change unless measurements later justify a separately approved design.

## Migration / rollback

The existing raster chunk subsystem is not deleted in this change. It remains available for stopped editing and as a short-term rollback path while temporal playback is validated.

Once the temporal renderer has passed the pathological chart and ordinary-chart regressions, a later cleanup can decide whether playback-specific raster prefetch/rebase code should be removed.
