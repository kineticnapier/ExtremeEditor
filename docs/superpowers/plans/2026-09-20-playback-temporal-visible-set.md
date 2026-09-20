# Playback Temporal Visible-Set Renderer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dense active-playback raster chunk composition with a playback-time-aware direct WPF floor/icon renderer so the pathological ~997,665-floor / 2.64e6 BPM chart stays visually correct without resize resets and sustains at least 30 FPS with Follow Player enabled.

**Architecture:** `TimingMap` supplies an O(log N) temporal floor range around the current chart time. During active dense playback, `LevelViewport` draws only that temporal range, then culls it against the current viewport and renders surviving stock floors/icons into a dedicated `_playbackFloorVisual`; the normal static/raster scene is hidden and raster maintenance is suspended until playback stops. Non-playback editing keeps the current static/raster scene path.

**Tech Stack:** C# / .NET 8, WPF `DrawingVisual` retained visuals, existing `TimingMap`, `WpfFloorRenderer`, `WpfIconRenderer`, console-style regression projects `ExtremeEditor.Core.Tests` and `ExtremeEditor.Wpf.Tests`.

**Spec:** `docs/superpowers/specs/2026-09-20-playback-temporal-visible-set-design.md`

## Global Constraints

- Work only on branch `feature/wpf-migration`.
- Preserve stock floor rendering and existing icon policy; no dots LOD, lower-resolution floor texture, stale-frame fallback, blank fallback, or fixed floor-count truncation in this change.
- Active dense playback must not call `RenderTargetBitmap.Render()`, wait for the raster worker, or increase raster chunk requests as Follow Player advances.
- Stopped/manual editing keeps the current static/raster renderer.
- The temporal selector uses binary search for temporal boundaries; no per-frame scan from floor 0 and no allocation proportional to total level floor count.
- Floors and icons in temporal mode use the current camera coordinate system, never `_sceneAnchorCamera`.
- Every code change bumps `EditorVersion.Current`.
- User workflow is strict TDD: commit RED, stop for the user's expected-failure verification, then commit GREEN and stop for the user's pass verification.
- Current version before implementation is `0.0.104-prototype`; planned sequence is `.105` RED / `.106` GREEN, `.107` RED / `.108` GREEN, `.109` RED / `.110` GREEN, `.111` RED / `.112` GREEN.
- Manual performance acceptance is Release-mode sustained `>=30 FPS` on the known pathological chart; do not encode wall-clock FPS thresholds in unit regressions.

## Review Focus

- **Equal-time floor runs at both temporal boundaries:** all floors whose entry time equals either inclusive boundary remain in the returned range; Task 1 pins lower-bound and upper-bound behavior.
- **Before-start / after-end playback:** selector clamps to a valid empty or partial range without indexing outside `TimingMap.Floors`; Task 1 tests both sides of the chart.
- **Viewport-edge floor geometry:** a floor center slightly outside the viewport but within the stock mesh margin remains eligible while a clearly distant floor is culled; Task 2 tests both cases.
- **Playback lifecycle transitions:** entering dense temporal mode hides/suspends the raster scene exactly once, stopping clears temporal drawing and restores a normal editor scene, and non-dense playback does not enter temporal mode; Task 3 tests all three paths.
- **Large Follow Player jumps:** temporal dense playback tracks the current camera directly and does not resume chunk requests or depend on scene rebasing; Task 3 advances through multiple far-apart poses and checks raster request count stays unchanged.

---

### Task 1: Temporal floor range selector

**Files:**
- Create: `src/ExtremeEditor.Core/PlaybackVisibleFloorSelector.cs`
- Modify: `src/ExtremeEditor.Core/TimingMap.cs`
- Create: `src/ExtremeEditor.Core.Tests/PlaybackVisibleFloorSelectorRegression.cs`
- Modify: `src/ExtremeEditor.Core.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Produces: `public readonly record struct PlaybackFloorRange(int StartFloor, int EndExclusive)` with `Count`.
- Produces: `public static PlaybackFloorRange PlaybackVisibleFloorSelector.Select(TimingMap timingMap, double chartTime, double pastWindowSeconds, double futureWindowSeconds)`.
- Produces: `public int TimingMap.FindFirstFloorAfter(double chartTime)`; returns the first floor whose entry time is strictly greater than `chartTime`, or `Floors.Count`.
- Later tasks consume the returned half-open floor range without allocating an array/list of floor indices.

- [ ] **Step 1: Add the RED regression for ordinary ranges, equal-time floors, chart boundaries, and narrow selection on a large map**

Create `PlaybackVisibleFloorSelectorRegression.cs` with the following structure:

```csharp
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class PlaybackVisibleFloorSelectorRegression
{
    public static void Run()
    {
        VerifyOrdinaryWindow();
        VerifyEqualTimesAreInclusive();
        VerifyChartBoundaries();
        VerifyLargeMapReturnsNarrowRange();
    }

    private static void VerifyOrdinaryWindow()
    {
        TimingMap map = CreateMap([0.0, 1.0, 2.0, 3.0, 4.0]);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 2.0, 0.5, 1.0);
        if (range.StartFloor != 2 || range.EndExclusive != 4)
            throw new InvalidOperationException($"Expected [2,4), actual=[{range.StartFloor},{range.EndExclusive}).");
    }

    private static void VerifyEqualTimesAreInclusive()
    {
        TimingMap map = CreateMap([0.0, 1.0, 1.0, 1.0, 2.0]);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 1.0, 0.0, 0.0);
        if (range.StartFloor != 1 || range.EndExclusive != 4)
            throw new InvalidOperationException($"Equal-time floors must all be selected. actual=[{range.StartFloor},{range.EndExclusive}).");
    }

    private static void VerifyChartBoundaries()
    {
        TimingMap map = CreateMap([0.0, 1.0, 2.0]);
        PlaybackFloorRange before = PlaybackVisibleFloorSelector.Select(map, -5.0, 0.1, 0.1);
        PlaybackFloorRange after = PlaybackVisibleFloorSelector.Select(map, 10.0, 0.1, 0.1);
        if (before.Count != 0 || after.Count != 0)
            throw new InvalidOperationException($"Far-outside windows must be empty. before={before.Count}, after={after.Count}.");
    }

    private static void VerifyLargeMapReturnsNarrowRange()
    {
        const int count = 200_000;
        var floors = new FloorTiming[count];
        for (int i = 0; i < count; i++)
            floors[i] = new FloorTiming(i, i * 0.001, i * 0.0015, 0, 0, Math.PI, 120, false, false);
        var map = new TimingMap(floors);
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(map, 100.0, 0.010, 0.010);
        if (range.Count > 25)
            throw new InvalidOperationException($"A narrow temporal query must stay narrow on a large map. count={range.Count}.");
    }

    private static TimingMap CreateMap(double[] entryTimes)
    {
        var floors = new FloorTiming[entryTimes.Length];
        for (int i = 0; i < entryTimes.Length; i++)
            floors[i] = new FloorTiming(i, entryTimes[i], entryTimes[i] + 0.5, 0, 0, Math.PI, 120, false, false);
        return new TimingMap(floors);
    }
}
```

Register `PlaybackVisibleFloorSelectorRegression.Run();` in `src/ExtremeEditor.Core.Tests/Program.cs` before its final PASS output.

- [ ] **Step 2: Bump RED version to `0.0.105-prototype` and commit only the regression/version wiring**

```csharp
public const string Current = "0.0.105-prototype";
```

Commit message:

```bash
git add src/ExtremeEditor.Core.Tests/PlaybackVisibleFloorSelectorRegression.cs src/ExtremeEditor.Core.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "test: require temporal playback floor selector"
```

- [ ] **Step 3: Stop for user RED verification**

Run requested from user:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
```

Expected first failure: missing `PlaybackFloorRange` or `PlaybackVisibleFloorSelector`; this is the intended RED. Do not write production code before the user confirms the expected failure.

- [ ] **Step 4: Implement strict upper bound in `TimingMap`**

Add next to `FindFirstFloorAtOrAfter`:

```csharp
public int FindFirstFloorAfter(double chartTime)
{
    int lo = 0;
    int hi = _entryTimes.Length;
    while (lo < hi)
    {
        int mid = lo + ((hi - lo) >> 1);
        if (_entryTimes[mid] <= chartTime)
            lo = mid + 1;
        else
            hi = mid;
    }
    return lo;
}
```

- [ ] **Step 5: Implement the allocation-free selector**

Create `PlaybackVisibleFloorSelector.cs`:

```csharp
namespace ExtremeEditor.Core;

public readonly record struct PlaybackFloorRange(int StartFloor, int EndExclusive)
{
    public int Count => Math.Max(0, EndExclusive - StartFloor);
}

public static class PlaybackVisibleFloorSelector
{
    public static PlaybackFloorRange Select(
        TimingMap timingMap,
        double chartTime,
        double pastWindowSeconds,
        double futureWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        if (pastWindowSeconds < 0) throw new ArgumentOutOfRangeException(nameof(pastWindowSeconds));
        if (futureWindowSeconds < 0) throw new ArgumentOutOfRangeException(nameof(futureWindowSeconds));

        double minTime = chartTime - pastWindowSeconds;
        double maxTime = chartTime + futureWindowSeconds;
        int start = timingMap.FindFirstFloorAtOrAfter(minTime);
        int endExclusive = timingMap.FindFirstFloorAfter(maxTime);
        if (endExclusive < start)
            endExclusive = start;
        return new PlaybackFloorRange(start, endExclusive);
    }
}
```

- [ ] **Step 6: Bump GREEN version to `0.0.106-prototype`, run the whole Core regression project, and commit**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
```

Expected: PASS with the new selector regression included.

Commit:

```bash
git add src/ExtremeEditor.Core/PlaybackVisibleFloorSelector.cs src/ExtremeEditor.Core/TimingMap.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: add temporal playback floor selector"
```

Stop for user GREEN verification before Task 2.

---

### Task 2: Dedicated direct playback floor visual and viewport culling

**Files:**
- Create: `src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Playback.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/TemporalPlaybackRenderingRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Consumes: `PlaybackVisibleFloorSelector.Select(...)` and `PlaybackFloorRange` from Task 1.
- Produces: dedicated `DrawingVisual _playbackFloorVisual` inserted between `_sceneRoot` and `_playbackVisual`.
- Produces diagnostic properties `TemporalPlaybackCandidateCount`, `TemporalPlaybackVisibleFloorCount`, `TemporalPlaybackVisibleIconCount`, `TemporalPlaybackDrawMilliseconds`, `TemporalPlaybackActive`.
- Produces private `RenderTemporalPlaybackFloors(TimingMap timingMap, double chartTime)`; Task 3 invokes it through playback-frame routing.
- Uses centralized constants `PlaybackPastVisibilitySeconds = 0.15`, `PlaybackFutureVisibilitySeconds = 0.50`, `PlaybackCullMarginWorld = 1.5f`. These are starting values for correctness/performance measurement, not a floor-count cap.

- [ ] **Step 1: Add RED WPF regression for visual ordering, viewport culling, and current-camera drawing contract**

Create `TemporalPlaybackRenderingRegression.cs` that uses reflection where needed and verifies:

```csharp
private static void VerifyPlaybackFloorVisualExistsBetweenSceneAndPlanets()
{
    PropertyInfo countProperty = typeof(LevelViewport).GetProperty(
        "VisualChildrenCount", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VisualChildrenCount missing.");
    var viewport = new LevelViewport();
    if ((int)countProperty.GetValue(viewport)! != 3)
        throw new InvalidOperationException("Temporal playback requires scene + playback floors + planet overlay visuals.");
}
```

Also require a private/static culling helper with deterministic behavior:

```csharp
MethodInfo cull = typeof(LevelViewport).GetMethod(
    "IntersectsPlaybackViewport",
    BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("LevelViewport.IntersectsPlaybackViewport is missing.");

var viewport = new WorldRect(-10, -10, 10, 10);
bool edge = (bool)cull.Invoke(null, [new Vector2(11.0f, 0), viewport, 1.5f])!;
bool far = (bool)cull.Invoke(null, [new Vector2(20.0f, 0), viewport, 1.5f])!;
if (!edge || far)
    throw new InvalidOperationException($"Playback viewport culling margin is wrong. edge={edge}, far={far}.");
```

Finally require the public diagnostic properties by name so the later renderer cannot silently omit measurement:

```text
TemporalPlaybackCandidateCount
TemporalPlaybackVisibleFloorCount
TemporalPlaybackVisibleIconCount
TemporalPlaybackDrawMilliseconds
TemporalPlaybackActive
```

Register this regression in WPF Tests `Program.cs`.

- [ ] **Step 2: Bump RED version to `0.0.107-prototype`, commit, and stop for user RED verification**

Run requested:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected first failure: `VisualChildrenCount` is still 2 or `_playbackFloorVisual` / temporal diagnostic members are absent.

Commit message:

```bash
git add src/ExtremeEditor.Wpf.Tests/TemporalPlaybackRenderingRegression.cs src/ExtremeEditor.Wpf.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "test: require temporal playback floor visual"
```

- [ ] **Step 3: Add `_playbackFloorVisual` to the visual tree**

In `LevelViewport.Playback.cs` add:

```csharp
private readonly DrawingVisual _playbackFloorVisual = new();
```

Change child order to:

```csharp
protected override int VisualChildrenCount => 3;

protected override Visual GetVisualChild(int index) => index switch
{
    0 => _sceneRoot,
    1 => _playbackFloorVisual,
    2 => _playbackVisual,
    _ => throw new ArgumentOutOfRangeException(nameof(index))
};
```

In `LevelViewport` constructor, add the child between the existing visuals:

```csharp
AddVisualChild(_sceneRoot);
AddVisualChild(_playbackFloorVisual);
AddVisualChild(_playbackVisual);
```

- [ ] **Step 4: Implement temporal floor drawing in a focused partial file**

Create `LevelViewport.TemporalPlayback.cs` with these members and behavior:

```csharp
using System.Diagnostics;
using System.Numerics;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const double PlaybackPastVisibilitySeconds = 0.15;
    private const double PlaybackFutureVisibilitySeconds = 0.50;
    private const float PlaybackCullMarginWorld = 1.5f;

    public bool TemporalPlaybackActive { get; private set; }
    public int TemporalPlaybackCandidateCount { get; private set; }
    public int TemporalPlaybackVisibleFloorCount { get; private set; }
    public int TemporalPlaybackVisibleIconCount { get; private set; }
    public double TemporalPlaybackDrawMilliseconds { get; private set; }

    private void RenderTemporalPlaybackFloors(TimingMap timingMap, double chartTime)
    {
        long started = Stopwatch.GetTimestamp();
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(
            timingMap,
            chartTime,
            PlaybackPastVisibilitySeconds,
            PlaybackFutureVisibilitySeconds);

        TemporalPlaybackCandidateCount = range.Count;
        TemporalPlaybackVisibleFloorCount = 0;
        TemporalPlaybackVisibleIconCount = 0;
        WorldRect viewport = GetViewportWorldRect();
        _floorRenderer.BeginFrame(_zoom);

        using DrawingContext dc = _playbackFloorVisual.RenderOpen();
        Vector2[] positions = _level!.Positions;
        for (int floor = range.EndExclusive - 1; floor >= range.StartFloor; floor--)
        {
            if ((uint)floor >= (uint)positions.Length)
                continue;
            Vector2 position = positions[floor];
            if (!IntersectsPlaybackViewport(position, viewport, PlaybackCullMarginWorld))
                continue;

            Point center = WorldToScreen(position);
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < _level.Angles.Length && Math.Abs(_level.Angles[floor] - 999.0) < 0.000001;
            _floorRenderer.DrawFloor(dc, center, _zoom, entryAngle, exitAngle, midSpin, selected: false);
            TemporalPlaybackVisibleFloorCount++;
        }

        TemporalPlaybackDrawMilliseconds =
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }

    private static bool IntersectsPlaybackViewport(Vector2 center, WorldRect viewport, float margin) =>
        center.X >= viewport.Left - margin && center.X <= viewport.Right + margin &&
        center.Y >= viewport.Top - margin && center.Y <= viewport.Bottom + margin;

    private void ClearTemporalPlaybackVisual()
    {
        using DrawingContext _ = _playbackFloorVisual.RenderOpen();
        TemporalPlaybackCandidateCount = 0;
        TemporalPlaybackVisibleFloorCount = 0;
        TemporalPlaybackVisibleIconCount = 0;
        TemporalPlaybackDrawMilliseconds = 0;
    }
}
```

Task 2 intentionally draws floors only; Task 4 adds icons using the same candidate/camera path rather than a separate coverage system.

- [ ] **Step 5: Bump GREEN version to `0.0.108-prototype`, run WPF regressions, and commit**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: PASS for visual-tree/culling/member regressions; existing raster regressions stay green because playback routing has not changed yet.

Commit:

```bash
git add src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs src/ExtremeEditor.Wpf/LevelViewport.Playback.cs src/ExtremeEditor.Wpf/LevelViewport.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: add temporal playback floor visual"
```

Stop for user GREEN verification before Task 3.

---

### Task 3: Route dense playback away from raster chunks and restore editor scene on stop

**Files:**
- Modify: `src/ExtremeEditor.Wpf/WpfPlaybackPresenter.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Playback.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/TemporalPlaybackRoutingRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/PlaybackViewportRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Produces: `internal void SetPlaybackFrame(TimingMap timingMap, double chartTime, PlaybackPose pose)`.
- Produces: `internal void ClearPlaybackFrame()`.
- `WpfPlaybackPresenter.Update(...)` computes `chartTime` once, then passes that same value and `timingMap.GetPose(level, chartTime)` to `SetPlaybackFrame`.
- Existing public `SetPlaybackPose(PlaybackPose?)` remains for compatibility/tests, but it is pose-only and does not activate temporal rendering because it lacks timing context.
- Dense temporal mode is entered when active playback has mesh preview enabled and the current static scene is already classified `StaticSceneRasterCacheActive`.

- [ ] **Step 1: Add RED routing regression**

Create a dense 5,000-floor viewport fixture (same tightly packed spacing pattern used by current dense regressions), arrange it at 800x600, render once so `StaticSceneRasterCacheActive` is true, then:

```csharp
int beforeRequests = viewport.RasterChunkRequestsQueued;
TimingMap map = TimingMapBuilder.Build(level);
double chartTime = map.GetEntryTime(Math.Min(100, level.FloorCount - 1));
PlaybackPose pose = map.GetPose(level, chartTime);
InvokeSetPlaybackFrame(viewport, map, chartTime, pose);

if (!viewport.TemporalPlaybackActive)
    throw new InvalidOperationException("Dense active playback must enter temporal rendering mode.");

int enteredRequests = viewport.RasterChunkRequestsQueued;
for (int i = 1; i <= 5; i++)
{
    int floor = Math.Min(100 + i * 500, level.FloorCount - 1);
    double t = map.GetEntryTime(floor);
    InvokeSetPlaybackFrame(viewport, map, t, map.GetPose(level, t));
}

if (viewport.RasterChunkRequestsQueued != enteredRequests)
    throw new InvalidOperationException("Temporal dense playback must not queue raster chunks while Follow Player advances.");
```

The fixture must set `FollowPlayer = true` and include a large spatial jump between some positions so the regression covers the previous rebase failure mode.

Also test stop lifecycle:

```csharp
InvokeClearPlaybackFrame(viewport);
if (viewport.TemporalPlaybackActive)
    throw new InvalidOperationException("Stopping playback must exit temporal mode.");
```

And non-dense behavior with `LevelDocument.CreateSynthetic(8)`:

```csharp
InvokeSetPlaybackFrame(nonDense, map, t, pose);
if (nonDense.TemporalPlaybackActive)
    throw new InvalidOperationException("Non-dense playback must keep the existing static scene path.");
```

Register in WPF `Program.cs`.

- [ ] **Step 2: Bump RED version to `0.0.109-prototype`, commit, and stop for user RED verification**

Expected failure: `SetPlaybackFrame` / `ClearPlaybackFrame` missing or raster request count grows under the old `SetPlaybackPose` path.

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Commit:

```bash
git add src/ExtremeEditor.Wpf.Tests/TemporalPlaybackRoutingRegression.cs src/ExtremeEditor.Wpf.Tests/PlaybackViewportRegression.cs src/ExtremeEditor.Wpf.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "test: require temporal dense playback routing"
```

- [ ] **Step 3: Change presenter to pass one chart time to pose and renderer**

Replace the active branch in `WpfPlaybackPresenter.Update` with:

```csharp
double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
PlaybackPose pose = timingMap.GetPose(level, chartTime);
viewport.SetPlaybackFrame(timingMap, chartTime, pose);
```

Replace the stopped branch with:

```csharp
viewport.ClearPlaybackFrame();
return;
```

- [ ] **Step 4: Implement temporal playback entry, update, and exit lifecycle**

In `LevelViewport.Playback.cs`, add `SetPlaybackFrame` and `ClearPlaybackFrame` while keeping `SetPlaybackPose` as a compatibility wrapper for pose-only drawing.

The dense temporal path must follow this order:

```csharp
internal void SetPlaybackFrame(TimingMap timingMap, double chartTime, PlaybackPose pose)
{
    ArgumentNullException.ThrowIfNull(timingMap);
    PlaybackPose = pose;

    bool useTemporal = _useFloorPreview &&
                       _zoom >= MinMeshPreviewZoom &&
                       (TemporalPlaybackActive || StaticSceneRasterCacheActive);

    if (useTemporal && !TemporalPlaybackActive)
        EnterTemporalPlaybackMode();

    if (FollowPlayer && _camera != pose.StationaryPlanet)
    {
        _camera = pose.StationaryPlanet;
        if (!TemporalPlaybackActive)
        {
            UpdateStaticSceneTransform();
            EnsureSceneCoverage();
        }
    }

    if (TemporalPlaybackActive)
        RenderTemporalPlaybackFloors(timingMap, chartTime);
    else if (StaticSceneRasterCacheActive)
        DrainCompletedRasterChunks();

    RenderPlaybackVisual();
}
```

`EnterTemporalPlaybackMode()` must:

```csharp
private void EnterTemporalPlaybackMode()
{
    TemporalPlaybackActive = true;
    _sceneRoot.Opacity = 0.0;
    ResetRasterChunks();
}
```

`ClearPlaybackFrame()` must:

```csharp
internal void ClearPlaybackFrame()
{
    PlaybackPose = null;
    bool wasTemporal = TemporalPlaybackActive;
    TemporalPlaybackActive = false;
    ClearTemporalPlaybackVisual();
    _sceneRoot.Opacity = 1.0;

    if (wasTemporal && _level is not null)
    {
        ResetStaticScene();
        EnsureSceneCoverage();
    }

    RenderPlaybackVisual();
}
```

Do not call `UpdateRasterChunksForViewport(playbackActive: true)` anywhere on the temporal path.

- [ ] **Step 5: Keep `SetPlaybackPose` behavior explicit and safe**

Refactor the existing method so pose-only callers can still test planets/follow behavior without accidentally entering temporal mode. It may continue the existing static/raster path because no timing map/chart time is available. Add a comment that production playback uses `SetPlaybackFrame`.

Update `PlaybackViewportRegression.VerifyPlaybackPresenterUpdatesAndClearsPose()` to expect the presenter to call the new frame API while preserving the externally visible `PlaybackPose` result.

- [ ] **Step 6: Bump GREEN version to `0.0.110-prototype`, run WPF regressions, and commit**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: routing/lifecycle regression PASS; raster request count remains constant after temporal mode entry; non-dense tests remain green.

Commit:

```bash
git add src/ExtremeEditor.Wpf/WpfPlaybackPresenter.cs src/ExtremeEditor.Wpf/LevelViewport.Playback.cs src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: route dense playback through temporal renderer"
```

Stop for user GREEN verification before Task 4.

---

### Task 4: Render icons in the temporal visual, expose diagnostics, and perform manual acceptance

**Files:**
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs`
- Modify: `src/ExtremeEditor.Wpf/LevelViewport.Rendering.cs`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.AudioDiagnostics.cs`
- Create: `src/ExtremeEditor.Wpf.Tests/TemporalPlaybackIconDiagnosticsRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/PlaybackDiagnosticsRegression.cs`
- Modify: `src/ExtremeEditor.Wpf.Tests/Program.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Changes private `DrawFloorIcon(...)` from `void` to `bool`; `true` means an icon image/event glyph was actually emitted to the `DrawingContext`, `false` means the floor had no active drawable icon.
- `RenderTemporalPlaybackFloors(...)` draws icons immediately after their floor using the same current-camera `center`, angles, and `midSpin` values.
- `PlaybackDiagnosticsSnapshot` adds `temporal=<candidateCount> visibleFloors=<count> visibleIcons=<count> temporalDraw=<ms>ms mode=<temporal|static>` while retaining raster counters for comparison.

- [ ] **Step 1: Add RED regression for icon-path integration and diagnostics fields**

The WPF regression must require:

```csharp
PropertyInfo icons = typeof(LevelViewport).GetProperty("TemporalPlaybackVisibleIconCount")
    ?? throw new InvalidOperationException("TemporalPlaybackVisibleIconCount missing.");
```

Use a dense action-bearing level with one visible active action floor and one clearly offscreen active action floor. Invoke temporal rendering at a chart time whose range contains both temporal entries. Assert the visible action floor can contribute to temporal icon handling while the far one is eliminated by viewport culling. Because local icon asset availability is environment-dependent, pin the deterministic pre-draw count with a new diagnostic property:

```csharp
public int TemporalPlaybackVisibleActionFloorCount { get; private set; }
```

Expected regression:

```csharp
if (viewport.TemporalPlaybackVisibleActionFloorCount != 1)
    throw new InvalidOperationException("Only the on-screen action-bearing floor should reach temporal icon rendering.");
```

Also extend `PlaybackDiagnosticsRegression` to require these substrings in `PlaybackDiagnosticsSnapshot`:

```text
temporal=
visibleFloors=
visibleIcons=
visibleActions=
temporalDraw=
mode=
```

- [ ] **Step 2: Bump RED version to `0.0.111-prototype`, commit, and stop for user RED verification**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected first failure: missing `TemporalPlaybackVisibleActionFloorCount` and/or diagnostics fields.

Commit:

```bash
git add src/ExtremeEditor.Wpf.Tests/TemporalPlaybackIconDiagnosticsRegression.cs src/ExtremeEditor.Wpf.Tests/PlaybackDiagnosticsRegression.cs src/ExtremeEditor.Wpf.Tests/Program.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "test: require temporal playback icons and diagnostics"
```

- [ ] **Step 3: Make `DrawFloorIcon` report whether it emitted an icon**

Change signature in `LevelViewport.Rendering.cs`:

```csharp
private bool DrawFloorIcon(
    DrawingContext drawingContext,
    int floor,
    Point center,
    float entryAngle,
    float exitAngle,
    bool midSpin)
```

Return `false` when `_level`/actions are absent or no active action exists. Change each successful branch from bare `return;` to `return true;`, and return `false` at the end when no cached icon/event asset could be drawn. Existing callers may ignore the returned bool.

- [ ] **Step 4: Draw temporal icons in the same loop and same camera transform as floors**

After each surviving floor draw in `RenderTemporalPlaybackFloors`:

```csharp
if (_level.ActionsByFloor.ContainsKey(floor))
{
    TemporalPlaybackVisibleActionFloorCount++;
    if (StaticSceneIconsEnabled &&
        DrawFloorIcon(dc, floor, center, entryAngle, exitAngle, midSpin))
    {
        TemporalPlaybackVisibleIconCount++;
    }
}
```

Reset `TemporalPlaybackVisibleActionFloorCount` together with the other counters at frame start and in `ClearTemporalPlaybackVisual()`.

Use the existing `StaticSceneIconsEnabled` zoom rule so this task does not redefine icon visibility policy.

- [ ] **Step 5: Extend diagnostic snapshot without removing raster evidence**

Update `MainWindow.AudioDiagnostics.cs` so the snapshot includes:

```csharp
$"temporal={Viewport.TemporalPlaybackCandidateCount} " +
$"visibleFloors={Viewport.TemporalPlaybackVisibleFloorCount} " +
$"visibleIcons={Viewport.TemporalPlaybackVisibleIconCount} " +
$"visibleActions={Viewport.TemporalPlaybackVisibleActionFloorCount} " +
$"temporalDraw={Viewport.TemporalPlaybackDrawMilliseconds:F2}ms " +
$"mode={(Viewport.TemporalPlaybackActive ? "temporal" : "static")} " +
```

Keep existing `chunks req/done/ready/pending/canceled/missing/syncDense` counters after these fields so the manual test can prove raster maintenance is inactive during temporal dense playback.

- [ ] **Step 6: Bump GREEN version to `0.0.112-prototype`, run both Core and WPF suites, and commit**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: both PASS with no new warnings/errors attributable to this work.

Commit:

```bash
git add src/ExtremeEditor.Wpf/LevelViewport.TemporalPlayback.cs src/ExtremeEditor.Wpf/LevelViewport.Rendering.cs src/ExtremeEditor.Wpf/MainWindow.AudioDiagnostics.cs src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: complete temporal playback rendering diagnostics"
```

Stop for user GREEN verification.

- [ ] **Step 7: Manual Release-mode pathological-chart acceptance**

Launch the WPF app in Release and open the known chart:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Wpf
```

Test conditions:

```text
~997,665 floors
2.64e6 BPM section
Follow Player ON
Hitsounds ON
Floor preview ON
Icon zoom threshold satisfied
No window resize during the run
```

Observe for at least 30 seconds through the previously failing high-speed area. Acceptance requires all of the following:

```text
mode=temporal
syncDense=0
raster chunks req does not increase after temporal mode entry
temporal candidate count remains local to the time window, not near total floor count
visibleFloors / visibleIcons remain plausible and update with the camera
no persistent missing/corrupted floor regions
no icon disappearance caused by stale coverage
no UCEERR_RENDERTHREADFAILURE
no resize needed to repair the scene
sustained fps >= 30
```

Record the diagnostic line at the worst observed high-speed section. If correctness passes but FPS is still below target, do not add a floor-count cap in this plan; use `temporal`, `visibleFloors`, `temporalDraw`, `rolling`, and actual frame cadence to identify the next bottleneck and design the next optimization separately.

---

## Plan self-review notes

- **Spec coverage:** selector, inclusive equal-time handling, current-camera viewport culling, dedicated playback visual, stock floor reuse, same-visual icons, dense routing, stopped-scene restoration, raster suspension, diagnostics, and manual >=30 FPS acceptance all have owning tasks.
- **No fixed `max_tile_show`:** intentionally absent per spec; the only reduction is temporal range plus viewport culling.
- **No raster deletion:** existing chunk code stays for editing/rollback; Task 3 only stops active dense playback from feeding it.
- **Type consistency:** Task 1 produces `PlaybackFloorRange`/`PlaybackVisibleFloorSelector`; Task 2 consumes them. Task 3 produces `SetPlaybackFrame`/`ClearPlaybackFrame`; presenter and tests consume those exact names. Task 4 consumes the temporal counters from Task 2 and adds one action-floor counter.
- **User TDD gate:** every task explicitly separates RED and GREEN commits/version bumps and requires stopping for local verification between them.
