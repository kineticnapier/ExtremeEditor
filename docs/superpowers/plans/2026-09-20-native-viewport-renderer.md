# Native Viewport Renderer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace WPF viewport drawing with a C++ native renderer hosted inside the existing WPF editor, while keeping editor state/semantics authoritative in C# and preserving the existing WPF viewport as a migration fallback.

**Architecture:** `ExtremeEditor.Wpf` hosts `ExtremeEditor.NativeRenderer.dll` through a flat C ABI and `HwndHost`. C# resolves level/timing/geometry/icon meaning into compact immutable snapshots; C++ owns the child HWND, D3D11/DXGI/Direct2D render loop, camera, culling, picking, and presentation. Playback is driven by a clock anchor so the native render thread can continue while the WPF Dispatcher is stalled.

**Tech Stack:** .NET 8 WPF, C# P/Invoke, C++20/MSVC, CMake, Win32, D3D11, DXGI, Direct2D, WIC, existing ExtremeEditor.Core/Rendering code.

**Spec:** `docs/superpowers/specs/2026-09-20-native-viewport-renderer-design.md`

## Global Constraints

- Work remains on `feature/wpf-migration`.
- Current starting version is `0.0.120-prototype`.
- Every code-changing RED and GREEN increments `EditorVersion`; docs-only commits do not.
- TDD gate is strict: RED -> user runs locally -> GREEN -> user runs locally. Stop after every RED and every GREEN.
- CI is not the acceptance gate; local Release-mode verification is authoritative.
- Do not merge without explicit instruction.
- C# remains authoritative for `LevelDocument`, `TimingMap`, ADOFAI semantics, audio transport, selection, undo/redo, save/load, and settings.
- C++ must not parse `.adofai` or reimplement `SetSpeed`, `Twirl`, `Pause`, or other editor semantics.
- Native ABI is C-only: no STL/C++ class layout/exceptions/COM pointers across the boundary.
- Native renderer uses Windows SDK only for the first implementation; no vcpkg or third-party renderer dependency.
- Native path must not use WPF `DrawingVisual`/`DrawingContext` floor rendering or WPF raster chunks.
- Minimum pathological-chart acceptance is sustained >=30 FPS; target is ~60 FPS or better.
- Existing WPF viewport stays available as fallback until native parity is manually accepted.
- Cleanup/removal of old WPF raster/temporal code is intentionally a later explicit task, not part of this plan.

## Review Focus

- **Missing/wrong native DLL or ABI version:** the app must report the mismatch and keep the WPF fallback usable rather than crashing at startup. Task 1 and Task 7 pin this.
- **Resize/minimize/device-loss lifecycle:** native resources must survive repeated resize/minimize/restore or surface an explicit recoverable error. Task 3 pins resize and device recreation behavior.
- **Million-floor input:** level load may be O(N), but no active frame may scan or marshal all floors; memory and queues must remain bounded. Tasks 4, 5, and 7 pin counts/diagnostics.
- **WPF Dispatcher stall during playback:** native `frame_count` and chart-time progression must continue while the managed UI thread sleeps. Task 5 pins this directly.
- **Curved/high-speed Follow Player:** current viewport membership must never depend solely on a temporal window, so floors already on screen cannot disappear/pop in because of admission boundaries. Task 5 pins spatial-current-view correctness and pose parity.

---

## File Structure

### New native project

- `src/ExtremeEditor.NativeRenderer/CMakeLists.txt` — DLL/test targets and Windows SDK linkage.
- `src/ExtremeEditor.NativeRenderer/include/extreme_editor_renderer.h` — stable public C ABI.
- `src/ExtremeEditor.NativeRenderer/src/exports.cpp` — ABI validation and exported function wrappers.
- `src/ExtremeEditor.NativeRenderer/src/renderer.h/.cpp` — renderer lifetime and thread-safe state ownership.
- `src/ExtremeEditor.NativeRenderer/src/native_window.h/.cpp` — child HWND and Win32 input/window messages.
- `src/ExtremeEditor.NativeRenderer/src/d2d_backend.h/.cpp` — D3D11/DXGI/D2D/WIC resources and drawing.
- `src/ExtremeEditor.NativeRenderer/src/renderer_math.h/.cpp` — camera transforms, binary search, culling, playback pose.
- `src/ExtremeEditor.NativeRenderer/src/spatial_grid.h/.cpp` — native stopped/current-viewport spatial candidate lookup.
- `src/ExtremeEditor.NativeRenderer/src/render_thread.h/.cpp` — independent frame loop and pacing.
- `src/ExtremeEditor.NativeRenderer/tests/native_renderer_tests.cpp` — no-third-party native regression executable.

### New managed native bridge

- `src/ExtremeEditor.Wpf/Native/NativeRendererStructs.cs` — exact ABI mirrors.
- `src/ExtremeEditor.Wpf/Native/NativeRendererNative.cs` — P/Invoke declarations only.
- `src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs` — safe lifetime/error wrapper.
- `src/ExtremeEditor.Wpf/Native/NativeLevelSnapshotBuilder.cs` — `LevelDocument`/`TimingMap` -> flat native snapshot, including geometry atlas.
- `src/ExtremeEditor.Wpf/Native/NativeLevelViewport.cs` — `HwndHost` and native viewport-facing WPF control.
- `src/ExtremeEditor.Wpf/Native/ViewportCoordinator.cs` — native/WPF routing and fallback during migration.

### Existing files modified across tasks

- `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj` — x64 + CMake build/copy target.
- `src/ExtremeEditor.Wpf/MainWindow.xaml` — host both viewports during migration.
- `src/ExtremeEditor.Wpf/MainWindow.xaml.cs` — route load/playback/frame/input through coordinator.
- `src/ExtremeEditor.Wpf/MainWindow.AudioDiagnostics.cs` — native diagnostics on active native path.
- `src/ExtremeEditor.Wpf/WpfFloorRenderer.cs` — no behavioral change; geometry quantization is mirrored by snapshot builder.
- `src/ExtremeEditor.Wpf/LevelViewport.Rendering.cs` — icon-policy extraction only when Task 6 needs it.
- `src/ExtremeEditor.Rendering/FloorIconResolver.cs` — shared C# icon semantic resolver created in Task 6.
- `src/ExtremeEditor.Wpf.Tests/Program.cs` — invoke each new managed regression.
- `src/ExtremeEditor.Core/EditorVersion.cs` — bump on every RED/GREEN code step.

---

### Task 1: Native ABI and build smoke

**Versions:** RED `0.0.121-prototype`, GREEN `0.0.122-prototype`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeRendererAbiRegression.cs`
- Modify RED/GREEN: `src/ExtremeEditor.Wpf.Tests/Program.cs`, `src/ExtremeEditor.Core/EditorVersion.cs`
- Create GREEN: `src/ExtremeEditor.NativeRenderer/CMakeLists.txt`
- Create GREEN: `src/ExtremeEditor.NativeRenderer/include/extreme_editor_renderer.h`
- Create GREEN: `src/ExtremeEditor.NativeRenderer/src/exports.cpp`
- Create GREEN: `src/ExtremeEditor.NativeRenderer/tests/native_renderer_tests.cpp`
- Create GREEN: `src/ExtremeEditor.Wpf/Native/NativeRendererStructs.cs`
- Create GREEN: `src/ExtremeEditor.Wpf/Native/NativeRendererNative.cs`
- Modify GREEN: `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`

**Interfaces:**
- Produces native API version `1`.
- Produces `EeAbiInfo` and managed `NativeAbiInfo` with exact sizes.
- Produces managed `NativeRendererNative.GetApiVersion()` and `GetAbiInfo(ref NativeAbiInfo)`.
- Later tasks consume the same DLL/build target and ABI header.

- [ ] **Step 1: Write the failing managed ABI regression and bump to `.121`**

Use reflection so RED fails cleanly before the bridge exists:

```csharp
internal static class NativeRendererAbiRegression
{
    public static void Run()
    {
        Type bridge = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        MethodInfo getApiVersion = bridge.GetMethod("GetApiVersion", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeRendererNative.GetApiVersion is missing.");
        uint version = (uint)(getApiVersion.Invoke(null, null) ?? 0u);
        if (version != 1u)
            throw new InvalidOperationException($"Native renderer API version mismatch: {version}.");
    }
}
```

Add `NativeRendererAbiRegression.Run();` to `Program.Main()` and set `EditorVersion.Current = "0.0.121-prototype"`.

- [ ] **Step 2: User runs RED locally**

Run:

```powershell
cmake --version
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected RED: `NativeRendererNative is missing.` If `cmake` itself is missing, stop and install/enable Visual Studio C++ build tools + CMake before GREEN.

- [ ] **Step 3: Add the minimal stable ABI and CMake target**

Public header begins with:

```cpp
#pragma once
#include <stdint.h>
#include <windows.h>

#define EE_RENDERER_API_VERSION 1u

typedef void* EeRendererHandle;

typedef enum EeResult {
    EE_OK = 0,
    EE_ERROR_INVALID_ARGUMENT = 1,
    EE_ERROR_ABI_MISMATCH = 2,
    EE_ERROR_INITIALIZATION = 3,
    EE_ERROR_DEVICE_LOST = 4
} EeResult;

typedef struct EeAbiInfo {
    uint32_t struct_size;
    uint32_t api_version;
    uint32_t floor_size;
    uint32_t clock_size;
    uint32_t diagnostics_size;
} EeAbiInfo;

extern "C" __declspec(dllexport) uint32_t ee_renderer_get_api_version(void);
extern "C" __declspec(dllexport) EeResult ee_renderer_get_abi_info(EeAbiInfo* info);
```

`exports.cpp` returns version 1 and validates `struct_size` before writing the rest of `EeAbiInfo`.

`CMakeLists.txt` uses C++20, builds `ExtremeEditor.NativeRenderer.dll`, and a `native_renderer_tests` executable. Link only Windows SDK libraries needed by the current task.

- [ ] **Step 4: Add managed P/Invoke and build integration**

Managed bridge:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAbiInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint FloorSize;
    public uint ClockSize;
    public uint DiagnosticsSize;
}

internal static partial class NativeRendererNative
{
    private const string DllName = "ExtremeEditor.NativeRenderer.dll";

    [LibraryImport(DllName, EntryPoint = "ee_renderer_get_api_version")]
    internal static partial uint GetApiVersion();

    [LibraryImport(DllName, EntryPoint = "ee_renderer_get_abi_info")]
    internal static partial int GetAbiInfo(ref NativeAbiInfo info);
}
```

Add x64 and CMake targets to `ExtremeEditor.Wpf.csproj`:

```xml
<PropertyGroup>
  <PlatformTarget>x64</PlatformTarget>
  <NativeRendererBuildDir>$(MSBuildThisFileDirectory)..\..\build\native-renderer</NativeRendererBuildDir>
</PropertyGroup>
<Target Name="BuildNativeRenderer" BeforeTargets="Build">
  <Exec Command="cmake -S &quot;$(MSBuildThisFileDirectory)..\ExtremeEditor.NativeRenderer&quot; -B &quot;$(NativeRendererBuildDir)&quot; -A x64" />
  <Exec Command="cmake --build &quot;$(NativeRendererBuildDir)&quot; --config $(Configuration)" />
</Target>
<Target Name="CopyNativeRenderer" AfterTargets="Build">
  <Copy SourceFiles="$(NativeRendererBuildDir)\$(Configuration)\ExtremeEditor.NativeRenderer.dll"
        DestinationFolder="$(OutDir)" />
</Target>
```

- [ ] **Step 5: Bump to `.122` and user runs GREEN**

Run:

```powershell
cmake -S src\ExtremeEditor.NativeRenderer -B build\native-renderer -A x64
cmake --build build\native-renderer --config Release
ctest --test-dir build\native-renderer -C Release --output-on-failure
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: native test PASS, managed ABI regression PASS.

- [ ] **Step 6: Commit Task 1 GREEN**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: bootstrap native renderer ABI"
```

---

### Task 2: Native child HWND, managed session, and HwndHost lifecycle

**Versions:** RED `.123`, GREEN `.124`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeRendererHostRegression.cs`
- Create GREEN: `src/ExtremeEditor.NativeRenderer/src/native_window.h`, `native_window.cpp`, `renderer.h`, `renderer.cpp`
- Modify GREEN: public ABI + `exports.cpp` + CMake
- Create GREEN: `src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs`
- Create GREEN: `src/ExtremeEditor.Wpf/Native/NativeLevelViewport.cs`
- Modify GREEN: `NativeRendererNative.cs`, `NativeRendererStructs.cs`

**Interfaces:**
- `NativeRendererSession.Create(nint parentHwnd, uint width, uint height)` -> disposable session.
- `NativeRendererSession.ChildHwnd` -> native child HWND.
- `NativeLevelViewport : HwndHost` owns one session and never exposes raw renderer ownership to MainWindow.

- [ ] **Step 1: RED regression and `.123`**

Test requires a disposable managed session and non-zero child HWND:

```csharp
using var source = new HwndSource(new HwndSourceParameters("NativeRendererHostRegression")
{
    Width = 320,
    Height = 200,
    WindowStyle = unchecked((int)0x80000000)
});

Type sessionType = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererSession")
    ?? throw new InvalidOperationException("NativeRendererSession is missing.");
MethodInfo create = sessionType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeRendererSession.Create is missing.");
object session = create.Invoke(null, [source.Handle, 320u, 200u])
    ?? throw new InvalidOperationException("NativeRendererSession.Create returned null.");
PropertyInfo child = sessionType.GetProperty("ChildHwnd")
    ?? throw new InvalidOperationException("NativeRendererSession.ChildHwnd is missing.");
if ((nint)(child.GetValue(session) ?? nint.Zero) == nint.Zero)
    throw new InvalidOperationException("Native child HWND was not created.");
((IDisposable)session).Dispose();
```

- [ ] **Step 2: User runs RED**

Expected: `NativeRendererSession is missing.`

- [ ] **Step 3: Add lifecycle ABI and Win32 child window**

Add exact ABI:

```c
typedef struct EeRendererCreateInfo {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t flags;
} EeRendererCreateInfo;

EeResult ee_renderer_create(HWND parent, const EeRendererCreateInfo* info, EeRendererHandle* out_renderer);
void ee_renderer_destroy(EeRendererHandle renderer);
HWND ee_renderer_get_child_hwnd(EeRendererHandle renderer);
void ee_renderer_resize(EeRendererHandle renderer, uint32_t width, uint32_t height);
```

`native_window.cpp` registers one process-wide window class with `RegisterClassExW`, creates a `WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN` window, and uses `DestroyWindow` during renderer destruction.

- [ ] **Step 4: Add managed session and HwndHost**

`NativeRendererSession` owns the opaque handle and translates non-zero `EeResult` into `InvalidOperationException` using `NativeRendererNative.GetLastError()` once Task 3 adds the error string; until then include the numeric code.

`NativeLevelViewport`:

```csharp
internal sealed class NativeLevelViewport : HwndHost
{
    private NativeRendererSession? _session;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        uint width = (uint)Math.Max(1, ActualWidth);
        uint height = (uint)Math.Max(1, ActualHeight);
        _session = NativeRendererSession.Create(hwndParent.Handle, width, height);
        return new HandleRef(this, _session.ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _session?.Dispose();
        _session = null;
    }
}
```

Override `OnRenderSizeChanged` to call `Resize` when a session exists.

- [ ] **Step 5: `.124` GREEN verification**

Run native CTest and WPF tests. Then launch `ExtremeEditor.Wpf` once and verify creating/destroying a standalone `NativeLevelViewport` test window does not crash on close.

- [ ] **Step 6: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: host native renderer HWND"
```

---

### Task 3: D3D11/DXGI/Direct2D render thread and diagnostics

**Versions:** RED `.125`, GREEN `.126`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeRendererRenderLoopRegression.cs`
- Create GREEN: `d2d_backend.h/.cpp`, `render_thread.h/.cpp`
- Modify: `renderer.*`, `native_window.*`, public ABI, managed structs/native/session.

**Interfaces:**
- `NativeRendererSession.GetDiagnostics()` -> `NativeRendererDiagnostics`.
- Native diagnostics include `FrameCount`, `FrameMilliseconds`, `MaxFrameMilliseconds`, `Width`, `Height`, `DeviceRecreateCount`, `LastPresentResult`.

- [ ] **Step 1: RED and `.125`**

Test creates a hidden parent/session, waits for native frames, resizes, and checks dimensions:

```csharp
NativeRendererDiagnostics before = session.GetDiagnostics();
long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
NativeRendererDiagnostics after = before;
while (Stopwatch.GetTimestamp() < deadline)
{
    Thread.Sleep(10);
    after = session.GetDiagnostics();
    if (after.FrameCount >= before.FrameCount + 3)
        break;
}
if (after.FrameCount < before.FrameCount + 3)
    throw new InvalidOperationException("Native render thread did not advance independently.");

session.Resize(640, 360);
Thread.Sleep(50);
NativeRendererDiagnostics resized = session.GetDiagnostics();
if (resized.Width != 640 || resized.Height != 360)
    throw new InvalidOperationException("Native resize was not applied.");
```

- [ ] **Step 2: User runs RED**

Expected: `GetDiagnostics`/render-loop capability missing.

- [ ] **Step 3: Implement device stack**

Create D3D11 device with `D3D11_CREATE_DEVICE_BGRA_SUPPORT`; create DXGI swap chain for child HWND; create D2D factory/device/context from the DXGI device; create a D2D target bitmap from swap-chain buffer.

Frame body is deliberately minimal:

```cpp
d2d_context_->BeginDraw();
d2d_context_->Clear(D2D1::ColorF(0x14161A));
HRESULT draw_hr = d2d_context_->EndDraw();
if (draw_hr == D2DERR_RECREATE_TARGET) recreate_targets();
HRESULT present_hr = swap_chain_->Present(1, 0);
```

Render thread owns active graphics objects. Resize is queued into renderer state and applied on the render thread before the next draw.

- [ ] **Step 4: Add device-loss/error handling**

When `Present` returns `DXGI_ERROR_DEVICE_REMOVED` or `DXGI_ERROR_DEVICE_RESET`, tear down D2D/DXGI/D3D resources and attempt one recreation loop. Increment `device_recreate_count`; keep the editor alive. Store last native error text in renderer-owned UTF-16 storage exposed by `ee_renderer_get_last_error`.

- [ ] **Step 5: `.126` GREEN**

Run native and managed tests. Manually resize/minimize/restore the test host repeatedly and verify `frame_count` keeps advancing after restore.

- [ ] **Step 6: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: add native Direct2D render loop"
```

---

### Task 4: C#-resolved level snapshot, geometry atlas, spatial grid, and static floor drawing

**Versions:** RED `.127`, GREEN `.128`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeLevelSnapshotRegression.cs`
- Create GREEN: `NativeLevelSnapshotBuilder.cs`
- Create GREEN native: `renderer_math.*`, `spatial_grid.*`, `level_snapshot.h/.cpp`
- Modify native backend/ABI and managed structs/session.

**Interfaces:**
- Managed `NativeLevelSnapshotBuilder.Build(LevelDocument, TimingMap)` -> `NativeLevelSnapshot`.
- Snapshot contains `NativeFloor[] Floors`, `NativeGeometry[] Geometries`, `NativePoint[] GeometryPoints`.
- `NativeRendererSession.LoadLevel(NativeLevelSnapshot snapshot)` copies data synchronously into native-owned vectors.
- `NativeRendererSession.SetView(NativeViewState view)` updates camera/zoom.

- [ ] **Step 1: RED snapshot tests and `.127`**

Pin flat layout and geometry deduplication:

```csharp
NativeLevelSnapshot snapshot = NativeLevelSnapshotBuilder.Build(level, timingMap);
if (snapshot.Floors.Length != level.FloorCount)
    throw new InvalidOperationException("Native snapshot floor count mismatch.");
if (snapshot.Floors[1].EntryTime != timingMap.Floors[1].EntryTime)
    throw new InvalidOperationException("Native timing copy mismatch.");
if (snapshot.Geometries.Length >= snapshot.Floors.Length)
    throw new InvalidOperationException("Repeated floor shapes must share geometry-atlas entries.");
if (Marshal.SizeOf<NativeFloor>() != 56)
    throw new InvalidOperationException($"NativeFloor ABI size changed: {Marshal.SizeOf<NativeFloor>()}.");
```

Use the same WPF quantization key: `Round(Mod(exit-entry, 2π) * 100000)` + `midSpin`.

- [ ] **Step 2: User runs RED**

Expected: `NativeLevelSnapshotBuilder` missing.

- [ ] **Step 3: Implement managed flat snapshot**

Define `NativeFloor` exactly:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFloor
{
    public uint Id;
    public uint Flags;
    public float X;
    public float Y;
    public float EntryAngle;
    public float ExitAngle;
    public float AngleMoved;
    public float PauseSeconds;
    public double EntryTime;
    public double ExitTime;
    public uint GeometryId;
    public uint IconKind;
}
```

For geometry atlas entries, call existing `AdoFaiFloorGeometryBuilder.Get(0f, delta, midSpin)` in C# and flatten `source.Main` into `NativePoint[]`. This avoids duplicating stock floor-shape semantics in C++.

- [ ] **Step 4: Implement native load + grid + floor draw**

`ee_renderer_load_level` receives floors, geometry headers, and points with counts. Validate all offsets/counts before copying.

Build a native uniform grid from floor centers once per load. `query(rect)` visits only intersecting grid cells and deduplicates floor IDs with a frame-generation stamp array.

Static/manual render path:

```cpp
auto candidates = spatial_grid_.query(viewport.expanded(1.5f));
for (uint32_t floor_id : candidates) {
    const EeFloor& floor = floors_[floor_id];
    if (!floor_intersects_view(floor, viewport)) continue;
    backend_.draw_floor(floor, geometries_[floor.geometry_id], geometry_points_);
}
```

Direct2D builds/caches `ID2D1PathGeometry` by geometry ID. World->screen transform is camera/zoom; edge thickness clamps to the same 1..5 px behavior as WPF.

- [ ] **Step 5: Add million-floor non-frame-scan regression**

Native test builds 1,000,000 points spread over grid cells, queries a small viewport, and asserts the returned candidate count is local (for the fixture, `< 10,000`) and `diagnostics.total_floor_scan_count == 0` for a render frame.

- [ ] **Step 6: `.128` GREEN**

Run CTest/WPF tests. Manually load an ordinary chart into a temporary native host and verify floors render, pan/zoom transforms are stable, and diagnostics report local candidate/visible counts.

- [ ] **Step 7: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: render native level snapshots"
```

---

### Task 5: Native playback clock, pose parity, Follow Player, and correct visible set

**Versions:** RED `.129`, GREEN `.130`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativePlaybackRegression.cs`
- Modify native math/renderer/render thread/ABI and managed bridge/session/viewport.

**Interfaces:**
- `NativeRendererSession.SetPlaybackClock(NativePlaybackClock clock)`.
- `NativeRendererSession.SetFollowPlayer(bool enabled)`.
- Test/debug API `TryGetPoseAt(double chartTime, out NativePlaybackPose pose)` for parity regression.

- [ ] **Step 1: RED `.129` cross-language playback regression**

Clock ABI:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct NativePlaybackClock
{
    public uint StructSize;
    public uint State; // 0 stopped, 1 paused, 2 running
    public double AnchorChartTime;
    public long AnchorTimestamp;
    public long TimestampFrequency;
    public double ChartRate;
}
```

For synthetic levels, compare native debug pose to `TimingMap.GetPose()` at representative times. Require position error <= `1e-4f` and progress error <= `1e-6` for ordinary CW, resolved CCW/twirl state, midspin, pause, high BPM, and a large floor index fixture.

Also require render-thread independence:

```csharp
session.SetPlaybackClock(clockRunningNow);
ulong before = session.GetDiagnostics().FrameCount;
double chartBefore = session.GetDiagnostics().ChartTime;
Thread.Sleep(200); // deliberately blocks the managed test/UI thread
NativeRendererDiagnostics after = session.GetDiagnostics();
if (after.FrameCount <= before)
    throw new InvalidOperationException("Native frames stopped with managed thread.");
if (after.ChartTime <= chartBefore + 0.10)
    throw new InvalidOperationException("Native playback clock did not advance independently.");
```

- [ ] **Step 2: User runs RED**

Expected: playback clock/pose API missing.

- [ ] **Step 3: Implement native clock and pose**

Native current chart time:

```cpp
double current_chart_time(const EePlaybackClock& c, int64_t now_qpc) {
    if (c.state != 2 || c.timestamp_frequency <= 0) return c.anchor_chart_time;
    const double elapsed = double(now_qpc - c.anchor_timestamp) / double(c.timestamp_frequency);
    return c.anchor_chart_time + elapsed * c.chart_rate;
}
```

Pose uses pre-resolved `entry_time`, `exit_time`, `pause_seconds`, `entry_angle`, `angle_moved`, direction flag, and floor position. C++ never applies ADOFAI offset/pitch semantics.

- [ ] **Step 4: Make current visibility spatially authoritative**

Do **not** let the temporal window decide whether an already-on-screen floor exists. Each playback frame queries the spatial grid for the current viewport and draws every intersecting floor. Temporal binary-search range remains for diagnostics/future-prefetch work, not as a destructive current-visibility filter.

Conceptual frame candidate flow:

```cpp
const auto temporal = timing_range(chart_time - 0.15, chart_time + 0.50);
auto current_visible_candidates = spatial_grid_.query(current_viewport.expanded(1.5f));
// current view wins for correctness; temporal range is lookahead/admission diagnostics.
```

This is the regression guard against the WPF temporal renderer's artificial appearance/disappearance behavior, including curved paths.

- [ ] **Step 5: Follow Player and camera**

When Follow Player is on, native camera center is the pose's stationary planet. When off, preserve manual camera. `SetView` from C# changes manual camera/zoom but does not fight Follow Player while playback is running.

- [ ] **Step 6: `.130` GREEN**

Run all tests. On the pathological chart, manually confirm camera/player keeps moving through a temporary WPF UI stall and no timing-window pop-in occurs.

- [ ] **Step 7: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: add native playback and follow camera"
```

---

### Task 6: Assets, icons, planets, selection, picking, and native input

**Versions:** RED `.131`, GREEN `.132`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeViewportParityRegression.cs`
- Create: `src/ExtremeEditor.Rendering/FloorIconResolver.cs`
- Modify: `LevelViewport.Rendering.cs` to use shared resolver without changing WPF behavior.
- Modify native backend/window/renderer/ABI and managed snapshot/session/viewport.

**Interfaces:**
- `NativeLevelViewport.SelectedFloorChanged` event.
- `NativeRendererSession.SetSelectedFloor(int floor)`.
- `NativeRendererSession.SetAssetPath(NativeAssetKind kind, string path)`.
- `NativeRendererSession.PickFloor(int x, int y)` for deterministic regression; HWND click path uses same picker.

- [ ] **Step 1: RED `.131` parity regression**

Require all of these:

```csharp
NativeLevelSnapshot snapshot = NativeLevelSnapshotBuilder.Build(levelWithTwirlAndSpeedIcon, timingMap);
if (snapshot.Floors[target].IconKind == 0)
    throw new InvalidOperationException("C# snapshot did not resolve the floor icon.");

session.LoadLevel(snapshot);
session.SetSelectedFloor(target);
int picked = session.PickFloor(screenX, screenY);
if (picked != target)
    throw new InvalidOperationException($"Native pick mismatch: {picked} != {target}.");
```

Also assert the extracted `FloorIconResolver` returns the same icon kind/path decision that the WPF renderer previously produced for SetSpeed/Twirl/Checkpoint/generic action fixtures.

- [ ] **Step 2: User runs RED**

Expected: shared resolver or native parity APIs missing.

- [ ] **Step 3: Extract icon semantics to C# shared resolver**

Move only the semantic decision into `ExtremeEditor.Rendering`:

```csharp
public enum FloorIconKind : uint
{
    None = 0,
    SetSpeed = 1,
    Twirl = 2,
    Checkpoint = 3,
    Event = 4
}

public static class FloorIconResolver
{
    public static FloorIconKind Resolve(IReadOnlyList<LevelAction> actions)
    {
        if (actions.Any(a => a.Active && a.EventType == "SetSpeed")) return FloorIconKind.SetSpeed;
        if (actions.Any(a => a.Active && a.EventType == "Twirl")) return FloorIconKind.Twirl;
        if (actions.Any(a => a.Active && a.EventType == "Checkpoint")) return FloorIconKind.Checkpoint;
        return actions.Any(a => a.Active) ? FloorIconKind.Event : FloorIconKind.None;
    }
}
```

Adapt WPF icon drawing to call this resolver, preserving current asset selection and appearance.

- [ ] **Step 4: Native WIC asset cache and drawing**

C# resolves paths with existing `AssetCache`/`IconAssetCache` and sends each path once. Native WIC decodes to D2D bitmaps and caches by `NativeAssetKind`.

Draw order inside native viewport:

```text
background/grid -> floors -> icons -> selection/hover -> planets
```

Planet rendering uses native playback pose. Selection uses the selected floor's cached geometry with a 2 px screen-space outline matching WPF intent.

- [ ] **Step 5: Native input and callback safety**

Child `WndProc` handles wheel/pan/click. Picker converts screen->world, queries nearby grid cells, and chooses the closest floor within the existing approximate selection radius (`0.856f` world units adjusted by zoom as appropriate).

Native callback is emitted from the child-window/UI thread, never the render thread. `NativeLevelViewport` converts it to WPF events and C# remains responsible for changing editor selection.

- [ ] **Step 6: `.132` GREEN**

Run tests. Manually verify ordinary chart: tile asset/fallback, icons, planets, selection, hover/picking, wheel zoom, pan, Follow Player toggle.

- [ ] **Step 7: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Rendering src/ExtremeEditor.Wpf src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: complete native viewport interactions"
```

---

### Task 7: MainWindow native-default routing, fallback, diagnostics, and pathological acceptance

**Versions:** RED `.133`, GREEN `.134`.

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeViewportRoutingRegression.cs`
- Create GREEN: `src/ExtremeEditor.Wpf/Native/ViewportCoordinator.cs`
- Modify: `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.AudioDiagnostics.cs`, managed native classes.

**Interfaces:**
- `ViewportCoordinator` chooses native unless unavailable/API mismatch or `EXTREMEEDITOR_WPF_VIEWPORT=1` forces fallback.
- `ViewportCoordinator.IsNativeActive`.
- `ViewportCoordinator.SetLevel(LevelDocument, SpatialGridIndex, TimingMap)`.
- `ViewportCoordinator.SetPlaybackClock(...)`, `StopPlayback()`, `FrameAll()`, `FollowPlayer`, `SelectedFloor`.

- [ ] **Step 1: RED `.133` routing regression**

Pin fallback without requiring a broken DLL on disk by injecting probe result:

```csharp
var fallback = new ViewportCoordinator(wpfViewport, nativeViewport, nativeAvailable: false);
if (fallback.IsNativeActive)
    throw new InvalidOperationException("Unavailable native renderer must use WPF fallback.");

var native = new ViewportCoordinator(wpfViewport, nativeViewport, nativeAvailable: true);
if (!native.IsNativeActive)
    throw new InvalidOperationException("Available native renderer must be preferred.");
```

Add an ABI mismatch test through `NativeRendererAvailability.ValidateAbi(apiVersion: 999, ...)` returning false with a readable reason.

- [ ] **Step 2: User runs RED**

Expected: `ViewportCoordinator` missing.

- [ ] **Step 3: Host both controls and centralize routing**

XAML:

```xml
<Grid>
    <local:LevelViewport x:Name="Viewport" />
    <local:NativeLevelViewport x:Name="NativeViewport" Visibility="Collapsed" />
</Grid>
```

`ViewportCoordinator` makes exactly one visible. On native activation, do not ask the WPF viewport to build dense raster/temporal playback state for the loaded level. On fallback, existing WPF behavior stays intact.

Environment override:

```csharp
bool forceWpf = string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_WPF_VIEWPORT"),
    "1",
    StringComparison.Ordinal);
```

- [ ] **Step 4: Route load/play/pause/seek/stop**

On level load, build one `NativeLevelSnapshot` and load it if native is active. Existing audio remains authoritative.

Every audio transport transition sends a new native anchor:

```csharp
new NativePlaybackClock
{
    StructSize = (uint)Marshal.SizeOf<NativePlaybackClock>(),
    State = _audio.IsPlaying ? 2u : _audio.IsStopped ? 0u : 1u,
    AnchorChartTime = PlaybackClock.AudioToChartTime(_level, _audio.Position.TotalSeconds),
    AnchorTimestamp = Stopwatch.GetTimestamp(),
    TimestampFrequency = Stopwatch.Frequency,
    ChartRate = Math.Max(0.000001, _level.PitchPercent * 0.01)
};
```

Periodic managed refresh may remain at 4 Hz for audio resynchronization/diagnostics; rendering must not depend on it.

- [ ] **Step 5: Native diagnostics in toolbar/log**

When native is active, snapshot text reports at least:

```text
native fps=... frame=...ms max=...ms candidates=... visible=... icons=... draws=... present=... deviceRecreate=...
```

Keep WPF diagnostics only on fallback. Do not update toolbar text faster than the existing 4 Hz gate.

- [ ] **Step 6: `.134` automated GREEN**

Run:

```powershell
ctest --test-dir build\native-renderer -C Release --output-on-failure
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
dotnet run -c Release --project src\ExtremeEditor.Audio.Tests
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

All must pass locally.

- [ ] **Step 7: Pathological manual acceptance before declaring GREEN complete**

Run the known `15. Singularity at 2.64e+6 BPM.adofai` in Release with Follow Player ON and collect native diagnostics. Acceptance:

```text
- native viewport actually active
- sustained >=30 FPS; target ~60+
- no periodic WPF-Dispatcher-induced camera freeze
- no artificial floor pop-in from temporal admission
- no full million-floor scan per frame
- no WPF raster chunk requests/builds on native path
- stable play/pause/seek/stop
- stable resize/minimize/restore
- icons/planets/selection correct enough for editor use
- no unbounded memory growth during extended playback
```

Then repeat once with:

```powershell
$env:EXTREMEEDITOR_WPF_VIEWPORT="1"
dotnet run -c Release --project src\ExtremeEditor.Wpf
```

and verify fallback still opens and behaves as before.

- [ ] **Step 8: Commit**

```powershell
git add src/ExtremeEditor.Wpf src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: make native viewport the default renderer"
```

---

## Post-plan cleanup boundary

Do **not** remove these during Tasks 1-7:

- `LevelViewport.RasterChunks.cs`
- `LevelViewport.TemporalPlayback.cs`
- `RasterChunkCache.cs`
- `RasterChunkWorker.cs`
- WPF renderer fallback tests

After `.134` is manually accepted on the pathological chart, create a separate bounded/architectural cleanup decision for removing obsolete WPF playback rendering. This preserves a known fallback while the native renderer is still new.
