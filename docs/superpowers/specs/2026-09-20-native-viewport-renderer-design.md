# Native Viewport Renderer Design

> **Historical snapshot:** The native-only production viewport described here is
> implemented. Migration fallback and branch wording below is archival. See the
> repository README and `docs/README.md` for the current architecture.

## Goal

Move ExtremeEditor's viewport rendering out of WPF retained-mode drawing and into a dedicated native C++ renderer while keeping the editor, data model, timing logic, audio control, undo/redo, file I/O, and general UI in C#/.NET/WPF.

The purpose is to make pathological charts, including the ~997,665-floor / 2.64e6 BPM test chart, render smoothly without depending on WPF `DrawingVisual`, `DrawingContext`, raster chunk composition, `DispatcherTimer` cadence, or repeated scene-graph reconstruction.

The target architecture is:

```text
ExtremeEditor.Wpf (C#)
├─ MainWindow / toolbar / dialogs
├─ LevelDocument / TimingMap
├─ audio transport
├─ editor commands / undo / redo
├─ save / load
└─ NativeLevelViewport : HwndHost
        │
        │ stable C ABI / PInvoke
        ▼
ExtremeEditor.NativeRenderer.dll (C++)
├─ child HWND
├─ render thread
├─ Direct3D 11 + Direct2D device stack
├─ camera / pan / zoom
├─ temporal + spatial culling
├─ floor / icon / planet / selection drawing
└─ hit testing / viewport input
```

The native renderer is a rendering subsystem, not a second editor implementation.

## Motivation

The WPF renderer has reached the point where the cost and correctness risk come from the retained rendering framework itself rather than from floor-selection math.

Recent diagnostics on the pathological chart showed all of the following:

- WPF playback timer gaps above 100 ms, including ~176 ms and larger,
- independent composition/render gaps above 100 ms,
- several thousand visible floors in a frame,
- `temporalDraw` measurements that did not include the final `DrawingContext.Dispose()` / retained-scene commit cost,
- dense playback mode changes causing large late-admission bursts and visible pop-in,
- repeated work to keep raster chunks, scene anchors, temporal retained sets, and WPF visuals coherent.

The editor already proved the useful ADOPAC-style idea: million-floor levels do not require drawing or scanning a million floors each frame. The remaining problem is that WPF is still asked to rebuild thousands of retained drawing commands at playback speed.

The native renderer therefore keeps the same high-level candidate strategy while replacing the final viewport presentation layer with an explicit real-time renderer.

## Scope

### In scope

- A new `ExtremeEditor.NativeRenderer` C++ DLL.
- A C ABI callable from C# through P/Invoke.
- A WPF `HwndHost` that owns the native child window.
- A dedicated native render thread independent from the WPF Dispatcher.
- Direct3D 11 / DXGI swap-chain ownership with Direct2D used as the first drawing backend.
- Full-level immutable rendering snapshots transferred from C# to C++ on level load.
- Incremental updates for edited floors/ranges after the initial load.
- Native playback-time interpolation from a C# supplied clock anchor.
- Temporal candidate selection plus viewport culling in native code.
- Native rendering of floors, icons, planets, selection, hover, background/grid, and other viewport overlays.
- Native mouse hit testing / pan / zoom with events bridged back to C#.
- Diagnostics and performance counters for the native renderer.
- Staged coexistence with the existing WPF viewport until native parity is reached.
- Tests for ABI layout, lifecycle, culling, timing, and integration.

### Out of scope

- Rewriting the whole editor in C++.
- Parsing `.adofai` in C++.
- Reimplementing `SetSpeed`, `Twirl`, `Pause`, or editor semantics in C++.
- Moving undo/redo, save/load, property editing, command routing, or toolbar UI to C++.
- Moving the audio engine to C++ in this change.
- Requiring D3D11 instancing in the first native renderer milestone.
- Removing the existing WPF renderer before the native path reaches functional parity.

## Ownership boundaries

### C# remains authoritative

C# is the source of truth for:

- `LevelDocument`,
- `TimingMap`,
- ADOFAI event semantics,
- audio transport state,
- selected floor,
- editor commands,
- undo/redo,
- clipboard,
- save/load,
- settings,
- asset discovery and configuration.

The native renderer must never become the canonical owner of level data.

If the native renderer is destroyed and recreated, C# must be able to rebuild its complete state from the editor model without reading anything back from native memory.

### C++ owns presentation state

C++ owns:

- native HWND,
- graphics device/context/swap chain,
- GPU resources,
- render thread,
- renderer-side copies of floor/timing/icon metadata,
- viewport camera state,
- temporal/spatial candidate structures,
- selection/hover presentation state,
- native hit testing,
- frame pacing and presentation.

The C++ side may cache derived data for rendering, but those caches are disposable and reconstructible.

## Native project and build

Add:

```text
src/ExtremeEditor.NativeRenderer/
├─ CMakeLists.txt
├─ include/
│  └─ extreme_editor_renderer.h
├─ src/
│  ├─ exports.cpp
│  ├─ renderer.cpp
│  ├─ render_thread.cpp
│  ├─ d2d_backend.cpp
│  ├─ viewport.cpp
│  └─ ...
└─ tests/
```

Use:

- MSVC,
- CMake,
- Windows SDK,
- Direct3D 11,
- DXGI,
- Direct2D,
- DirectWrite only if needed for viewport text,
- WIC for bitmap/icon loading.

Do not add vcpkg or third-party rendering frameworks in the first version.

The WPF project build must be able to invoke/configure the native CMake project and copy `ExtremeEditor.NativeRenderer.dll` next to the WPF executable for local Release/Debug runs.

The native build requirement should fail with a clear message if the Visual C++ workload / Windows SDK is missing.

## Stable C ABI

The C# / C++ boundary must be a flat C ABI, not exported C++ classes.

Exports use:

```cpp
extern "C" __declspec(dllexport)
```

and fixed-width integer types.

Do not expose STL containers, C++ references, C++ exceptions, COM smart pointers, or compiler-specific class layouts across the boundary.

The ABI must include an API version and structure-size checks so mismatched managed/native binaries fail explicitly.

Representative API shape:

```c
uint32_t ee_renderer_get_api_version(void);

EeResult ee_renderer_create(
    HWND parent,
    const EeRendererCreateInfo* create_info,
    EeRendererHandle* out_renderer);

void ee_renderer_destroy(EeRendererHandle renderer);

EeResult ee_renderer_load_level(
    EeRendererHandle renderer,
    const EeFloor* floors,
    uint32_t floor_count,
    const EeIcon* icons,
    uint32_t icon_count);

EeResult ee_renderer_update_floor_range(
    EeRendererHandle renderer,
    uint32_t start_floor,
    const EeFloor* floors,
    uint32_t floor_count);

void ee_renderer_set_selection(EeRendererHandle renderer, int32_t floor);
void ee_renderer_set_follow_player(EeRendererHandle renderer, uint32_t enabled);
void ee_renderer_set_clock(EeRendererHandle renderer, const EePlaybackClock* clock);
void ee_renderer_set_view(EeRendererHandle renderer, const EeViewState* view);

EeResult ee_renderer_set_event_callback(
    EeRendererHandle renderer,
    EeRendererEventCallback callback,
    void* user_data);
```

Exact names may change during implementation, but the boundary must preserve these responsibilities.

## Flat rendering snapshot

The initial full-level transfer uses compact sequential structs that contain only data required to render and animate.

A representative floor record is:

```c
struct EeFloor
{
    uint32_t id;
    float x;
    float y;

    float entry_angle;
    float exit_angle;
    float angle_moved;
    float pause_seconds;

    double entry_time;
    double exit_time;

    uint32_t flags;
    uint32_t icon_start;
    uint32_t icon_count;
};
```

Flags may represent already-resolved rendering facts such as:

- clockwise/counter-clockwise,
- midspin,
- special floor appearance,
- any other renderer-visible state.

They must not encode raw ADOFAI event semantics that force C++ to understand editor rules.

C# builds this snapshot from `LevelDocument` + `TimingMap`.

The full snapshot is transferred once on level load. A million floors must not be marshalled every frame.

Editing uses range updates or a later dirty-range batching mechanism.

## Playback clock handoff

The render thread must not depend on WPF `DispatcherTimer` callbacks for each frame.

C# supplies a clock anchor containing at least:

- chart time at anchor,
- high-resolution QPC/Stopwatch timestamp at anchor,
- running/paused/stopped state,
- effective chart-seconds-per-wall-second rate.

The native render thread extrapolates current chart time from that anchor while playback is running.

Conceptually:

```text
chartTime(now) = anchorChartTime
               + elapsedQpcSeconds * chartRate
```

C# refreshes the anchor periodically from the authoritative audio transport and pushes immediate anchors on:

- Play,
- Pause,
- Stop,
- Seek,
- pitch/rate changes,
- level reload.

This allows the viewport to continue moving smoothly during a temporary WPF Dispatcher stall while still periodically resynchronizing to the audio transport.

C++ does not implement `PlaybackClock` or offset/pitch semantics. C# converts those semantics into the anchor/rate supplied to native code.

## Native playback pose

To avoid WPF stalls freezing Follow Player, the native renderer computes the visual playback pose from pre-resolved floor timing records.

This is rendering interpolation, not ADOFAI semantic evaluation.

For a frame:

1. obtain current chart time from the native clock anchor,
2. binary-search floor timing by entry time,
3. interpolate progress using precomputed timing fields,
4. obtain the stationary floor position,
5. calculate the orbiting planet position from precomputed entry angle / angle moved / direction,
6. use the stationary floor as camera target when Follow Player is enabled.

The interpolation result should match the existing C# `TimingMap.GetPose()` path within an agreed numeric tolerance.

A cross-language regression must compare representative native pose results against C# pose results.

## Candidate selection and culling

The native renderer keeps the ADOPAC-inspired strategy:

```text
chartTime
    ↓ binary search
small temporal range
    ↓ spatial / viewport cull
visible floor set
    ↓
draw
```

The renderer must not linearly scan one million floors each frame.

The first implementation may use:

- binary search on entry times for temporal bounds,
- direct iteration over that bounded temporal range during playback,
- a simple native uniform-grid or equivalent spatial index for stopped/manual editing.

The exact native spatial structure is a renderer concern and may be rebuilt from floor positions at level load.

The playback temporal window is an admission/candidate mechanism only. It must not create artificial floor appearance/disappearance at the viewport edge.

The native path must preserve sufficient future coverage so floors are available before the camera reaches them, including curved paths rather than only a current/future endpoint bounding box.

## Rendering backend

### Device stack

The initial backend uses:

```text
D3D11 device (BGRA support)
    ↓
DXGI swap chain for child HWND
    ↓
D2D device/context targeting the swap-chain buffer
```

This gives an explicit swap-chain / presentation model while allowing the first implementation to use Direct2D for floor geometry and bitmap icons.

### Why Direct2D first

Direct2D maps naturally to the current 2D content:

- filled/stroked floor geometry,
- lines,
- bitmap icons,
- planets,
- selection/hover outlines,
- background/grid.

It reduces the initial porting cost compared with introducing shaders, vertex formats, instance buffers, and atlases immediately.

### D3D11 instancing as a later backend optimization

The architecture must not bake Direct2D calls into the C# ABI.

If profiling shows floor draw calls are still the dominant cost, the native backend may later replace floor rendering with D3D11 instancing while reusing:

- the same `HwndHost`,
- the same native renderer handle,
- the same level snapshot,
- the same playback clock,
- the same culling and event contracts,
- the same swap chain/device stack where practical.

This is why the public boundary is `renderer_*`, not `direct2d_*`.

## Render thread

The child HWND is created/owned from the WPF hosting lifecycle, but drawing must be performed by a dedicated native render thread.

The render thread owns mutable graphics resources that must not be concurrently used by the WPF UI thread.

UI/native-window events communicate with the render thread through thread-safe state snapshots/queues rather than directly mutating active D2D/D3D objects.

The render loop should:

1. wait for work / frame deadline,
2. snapshot current clock/view/selection state,
3. derive current playback pose,
4. select/cull visible content,
5. render a frame,
6. present,
7. record diagnostics.

Initial frame pacing target: monitor-synchronized presentation or an explicit practical cap around 120 FPS. The first milestone only needs to prove stable >=30 FPS on the pathological chart; final pacing policy can be refined from measurements.

## WPF host and HWND airspace

Add a WPF `NativeLevelViewport : HwndHost`.

It creates/destroys the native child HWND through the renderer API.

Because HWND airspace prevents normal WPF overlay composition on top of the child window, all viewport-internal visuals must ultimately be native-rendered:

- floors,
- icons,
- planets,
- selection,
- hover,
- background/grid,
- viewport diagnostics if any are intended to overlay the scene.

Normal editor chrome remains WPF:

- toolbar,
- status bar,
- side/property panels,
- dialogs,
- menus.

Do not design a final state that depends on WPF planet/selection overlays floating over the `HwndHost`.

## Input and events

Mouse input for the child HWND is handled in native code.

Native responsibilities include:

- pointer position conversion,
- pan gestures,
- wheel zoom,
- hit testing / floor picking,
- hover detection.

Editor-semantic actions are reported to C# through a callback/event bridge on the window/UI thread.

Representative events:

- floor clicked,
- floor double-clicked,
- hover changed,
- camera changed by manual pan/zoom,
- native renderer error/device-loss status.

C# then performs the editor action, e.g. setting `SelectedFloor`, and sends resulting presentation state back to native code.

Callbacks must never invoke editor code from the native render thread.

## Asset handling

C# remains responsible for discovering/configuring stock floor and icon assets.

The native renderer should receive resolved asset information rather than hard-code repository/game installation paths.

The first implementation may pass absolute image paths to native code and let WIC decode them into native GPU resources.

Asset loading is not a per-frame operation.

The renderer should cache:

- floor texture/brush resources,
- icon bitmaps,
- reusable floor geometry where useful,
- any derived GPU resources.

## Editing updates

The native renderer must support editor changes without re-sending the entire million-floor snapshot for every small edit.

The first supported contract is dirty range replacement:

```text
C# mutates LevelDocument
    ↓
rebuild affected path/timing range
    ↓
renderer_update_floor_range(start, data, count)
```

If the editor operation changes all subsequent positions/timings, a large range or full snapshot reload is acceptable initially.

More sophisticated patching is not required before profiling demonstrates a need.

## Failure behavior

Native renderer failures must not corrupt editor state.

Required behavior:

- native API returns explicit result codes,
- C++ exceptions do not cross the ABI,
- last-error text is retrievable for diagnostics,
- device-lost conditions attempt graphics-resource recreation,
- unrecoverable native initialization/rendering failure is surfaced to C#,
- during migration, C# can fall back to the existing WPF viewport instead of terminating the editor.

Once the native renderer becomes the only viewport implementation, fallback policy may be revisited separately.

## Migration strategy

Do not delete the existing WPF viewport at the beginning.

### Milestone 1: native host / ABI smoke test

- C++ DLL builds.
- `HwndHost` creates a child HWND.
- swap chain clears to a test background.
- resize and destruction are stable.
- API version/layout checks pass.

### Milestone 2: level snapshot + floor rendering

- C# converts level/timing data to native structs.
- native renderer draws floor geometry for ordinary levels.
- viewport pan/zoom works.
- renderer diagnostics report candidate/visible counts and frame time.

### Milestone 3: playback renderer

- native playback clock anchor works.
- native temporal selection works.
- native pose matches C#.
- Follow Player works independently of WPF frame cadence.
- pathological chart is playable through the native surface.

### Milestone 4: viewport parity

- icons,
- planets,
- selection,
- hover,
- picking,
- editor navigation,
- required stock appearance.

### Milestone 5: native default

- native viewport becomes the default path.
- WPF viewport remains temporarily available as an internal fallback/comparison path.
- pathological acceptance tests are rerun.

### Milestone 6: cleanup

Only after native parity and manual acceptance:

- remove obsolete WPF playback raster/temporal machinery,
- remove no-longer-needed WPF viewport rendering code,
- simplify diagnostics around the new renderer.

Cleanup is a separate explicit step, not mixed into initial native bring-up.

## Diagnostics

Expose native counters to C# without per-floor logging.

At minimum:

- render FPS / present cadence,
- current frame render ms,
- rolling/max render ms,
- current chart time,
- temporal candidate count,
- visible floor count,
- visible icon count,
- culled count,
- draw-call count,
- swap-chain present duration/result,
- device-lost/recreate count,
- renderer queue backlog if applicable.

Diagnostics should be queryable as a compact snapshot struct.

The existing `playback-diagnostics.log` mechanism may incorporate these snapshots during migration.

## Testing

### Native unit tests

Use a small C++ test executable / CTest target without adding a third-party test framework initially.

Cover:

- temporal lower/upper-bound search,
- viewport culling,
- curved-path future coverage logic,
- playback pose interpolation,
- camera transform math,
- hit testing,
- ABI version/struct-size checks.

### Managed tests

Add WPF/Core regressions for:

- C# native snapshot conversion,
- struct layout/size agreement,
- API-version mismatch behavior,
- native lifecycle wrapper disposal,
- fallback behavior when DLL/init is unavailable,
- clock-anchor construction,
- dirty-range update selection.

### Cross-language pose regression

For representative synthetic timing maps, compare native playback pose against `TimingMap.GetPose()` within a fixed tolerance.

Include:

- ordinary clockwise floors,
- twirl-resolved direction,
- midspins,
- pauses,
- high BPM,
- large floor indices.

### Manual acceptance

Release-mode tests must include the known ~997,665-floor chart.

Observe:

- Follow Player on,
- no artificial floor pop-in caused by renderer admission boundaries,
- no periodic WPF-Dispatcher-induced camera freeze,
- no native crash/device-loss loop,
- correct floor/icon/planet/selection appearance,
- stable resize/minimize/restore,
- stable play/pause/seek/stop,
- no unbounded memory growth.

## Performance requirements

Minimum acceptance on the pathological chart:

- sustained >=30 FPS during active playback,
- target ~60 FPS or better where hardware allows,
- no frame work proportional to total million-floor count,
- no full-level managed/native transfer per frame,
- no WPF `DrawingVisual`/`DrawingContext` floor rendering on the native path,
- no WPF raster chunk generation on the native path,
- UI Dispatcher stalls must not directly stop the native render loop,
- memory usage must remain bounded over extended playback.

The first native implementation should prioritize correctness and explicit measurements. If Direct2D draw-call overhead remains dominant after WPF is removed from the viewport, D3D11 floor instancing becomes the next measured optimization rather than an assumption made upfront.

## Versioning and workflow

- All implementation changes remain on `feature/wpf-migration` unless explicitly changed later.
- Every code-changing RED/GREEN step increments `EditorVersion` according to the existing project rule.
- Docs-only commits do not increment `EditorVersion`.
- Existing TDD workflow remains: RED -> local user verification -> GREEN -> local user verification.
- The current WPF renderer stays available until the native path reaches the milestone being tested; no destructive cleanup before acceptance.

## Final architectural decision

ExtremeEditor remains primarily a C#/.NET/WPF editor.

Only the viewport rendering subsystem becomes native C++.

The first backend is Direct2D over a D3D11/DXGI swap chain, with the architecture intentionally preserving the option to replace the floor drawing stage with D3D11 instancing later without changing the managed/native contract.

The editor owns meaning. The native renderer owns pixels.
