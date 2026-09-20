# Native Viewport Edit Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add incremental C# -> native renderer synchronization for edited floor/timing ranges so small editor changes do not require retransferring a million-floor snapshot.

**Architecture:** This is a required companion task to `2026-09-20-native-viewport-renderer.md`. Execute it **after Task 6 and before that plan's Task 7**. C# remains authoritative and rebuilds the affected resolved render records; native code atomically replaces the corresponding renderer-owned range and rebuilds only spatial-index cells touched by the replacement.

**Tech Stack:** .NET 8, C# P/Invoke, existing C++ native renderer ABI/spatial grid.

**Spec:** `docs/superpowers/specs/2026-09-20-native-viewport-renderer-design.md`

## Global Constraints

- Work remains on `feature/wpf-migration`.
- TDD: RED -> local user verification -> GREEN -> local user verification.
- Every code-changing RED/GREEN increments `EditorVersion`.
- Run this after main-plan Task 6 (`0.0.132-prototype`).
- This task uses RED `0.0.133-prototype` and GREEN `0.0.134-prototype`.
- Therefore the original version labels on main-plan Task 7 are superseded: main-plan Task 7 becomes RED `0.0.135-prototype`, GREEN `0.0.136-prototype`.
- Do not merge without explicit instruction.

## Review Focus

- A replacement range at floor 0 or the final floor must not underflow/overflow.
- Replacement records must keep floor IDs contiguous and equal to their array indices.
- Geometry-atlas additions must not invalidate existing geometry IDs.
- A range whose positions change grid cells must remove stale spatial memberships.
- Render thread must never observe a partially updated floor range.

---

### Task 6.5: Dirty-range replacement

**Files:**
- Create RED: `src/ExtremeEditor.Wpf.Tests/NativeRendererEditSyncRegression.cs`
- Modify RED/GREEN: `src/ExtremeEditor.Wpf.Tests/Program.cs`, `src/ExtremeEditor.Core/EditorVersion.cs`
- Modify GREEN: `src/ExtremeEditor.Wpf/Native/NativeLevelSnapshotBuilder.cs`
- Modify GREEN: `src/ExtremeEditor.Wpf/Native/NativeRendererSession.cs`
- Modify GREEN: `src/ExtremeEditor.Wpf/Native/NativeRendererNative.cs`
- Modify GREEN: `src/ExtremeEditor.NativeRenderer/include/extreme_editor_renderer.h`
- Modify GREEN: `src/ExtremeEditor.NativeRenderer/src/exports.cpp`, `renderer.*`, `spatial_grid.*`, `level_snapshot.*`

**Interfaces:**
- `NativeLevelSnapshotBuilder.BuildFloorRange(LevelDocument level, TimingMap timingMap, int startFloor, int count, NativeGeometryAtlas atlas)` -> `NativeFloorRangeUpdate`.
- `NativeRendererSession.UpdateFloorRange(int startFloor, NativeFloorRangeUpdate update)`.
- Native ABI: `ee_renderer_update_floor_range(handle, start_floor, floors, floor_count, new_geometries, geometry_count, new_points, point_count)`.

- [ ] **Step 1: Write RED and bump to `.133`**

Regression starts with a 4-floor snapshot, moves floors 1-2, sends only that range, and checks native debug state:

```csharp
NativeLevelSnapshot initial = NativeLevelSnapshotBuilder.Build(level, timingMap);
session.LoadLevel(initial);

level.Positions[1] = new Vector2(50f, 10f);
level.Positions[2] = new Vector2(51f, 10f);
NativeFloorRangeUpdate update = NativeLevelSnapshotBuilder.BuildFloorRange(
    level,
    timingMap,
    startFloor: 1,
    count: 2,
    initial.GeometryAtlas);
session.UpdateFloorRange(1, update);

NativeFloorDebug floor1 = session.GetFloorDebug(1);
NativeFloorDebug floor0 = session.GetFloorDebug(0);
if (Math.Abs(floor1.X - 50f) > 1e-6f)
    throw new InvalidOperationException("Dirty-range floor was not updated.");
if (Math.Abs(floor0.X - initial.Floors[0].X) > 1e-6f)
    throw new InvalidOperationException("Dirty-range update modified an untouched floor.");
```

Also query the old and new spatial regions and assert floor 1 is absent from the old cell and present in the new cell.

- [ ] **Step 2: User runs RED**

Expected: `BuildFloorRange` or `UpdateFloorRange` is missing.

- [ ] **Step 3: Implement append-only geometry atlas updates**

`NativeGeometryAtlas` preserves all existing geometry IDs. When an edited floor introduces a new `(quantizedDelta, midSpin)` key, append one geometry header and its points; never renumber prior geometry IDs.

`BuildFloorRange` validates:

```csharp
if (startFloor < 0 || count < 0 || startFloor + count > level.FloorCount)
    throw new ArgumentOutOfRangeException();
```

and emits floor records whose `Id == startFloor + localIndex`.

- [ ] **Step 4: Implement native atomic replacement**

On the exported call, validate all IDs/geometry offsets before acquiring the renderer state lock. Build temporary vectors first, then under one write lock:

```cpp
for (uint32_t i = 0; i < floor_count; ++i)
    floors_[start_floor + i] = validated_floors[i];
geometry_headers_.insert(geometry_headers_.end(), new_geometries.begin(), new_geometries.end());
geometry_points_.insert(geometry_points_.end(), new_points.begin(), new_points.end());
spatial_grid_.replace_range(start_floor, floor_count, floors_);
level_generation_.fetch_add(1, std::memory_order_release);
```

Render thread snapshots the generation/state only between frames, so it never draws a half-applied range.

- [ ] **Step 5: Cover boundaries**

Add native/managed cases for:

```text
start=0,count=1
start=floorCount-1,count=1
count=0 (no-op)
out-of-range start/count (explicit error)
```

and a range that changes position across grid cells.

- [ ] **Step 6: Bump to `.134` and user runs GREEN**

Run:

```powershell
ctest --test-dir build\native-renderer -C Release --output-on-failure
dotnet run -c Release --project src\ExtremeEditor.Wpf.Tests
```

Expected: all edit-sync, ABI, lifecycle, render-loop, snapshot, playback, and parity regressions PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/ExtremeEditor.NativeRenderer src/ExtremeEditor.Wpf/Native src/ExtremeEditor.Wpf.Tests src/ExtremeEditor.Core/EditorVersion.cs
git commit -m "feat: synchronize native dirty floor ranges"
```

After this task, continue main-plan Task 7 using `.135` RED and `.136` GREEN.
