# WPF Slice 3 Minimal Viewport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WPF placeholder with a minimal custom viewport that renders a synthetic level through `DrawingContext`, uses `SpatialGridIndex` culling, and supports frame-all, pan, zoom, and nearest-floor selection.

**Architecture:** Add one `FrameworkElement`-derived `LevelViewport` in `ExtremeEditor.Wpf`. The viewport owns camera state and input, queries the existing Core spatial index for visible candidates, and draws only simple lines/dots. `MainWindow` hosts the element and enables only the `Frame` action; all unrelated editor actions remain disabled.

**Tech Stack:** .NET 8, WPF, `DrawingContext`, `System.Numerics`, existing `ExtremeEditor.Core`

**Spec:** `docs/superpowers/specs/2026-09-18-wpf-migration-design.md`

## Global Constraints

- All work remains on `feature/wpf-migration`.
- Do not create one WPF control or visual per floor.
- Reuse `LevelDocument`, `WorldRect`, and `SpatialGridIndex` from Core.
- Do not port GDI floor textures, event icons, audio, file loading, save/edit operations, or playback in this slice.
- Keep the existing WinForms app unchanged.
- Bump `EditorVersion` from `0.0.53-prototype` to `0.0.54-prototype`.
- This slice contains direct WPF rendering/input behavior; verification is WPF build, existing Core tests, WinForms build, and manual interaction in the launched WPF app.

---

### Task 1: Add the minimal WPF viewport

**Files:**
- Create: `src/ExtremeEditor.Wpf/LevelViewport.cs`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- `LevelViewport.SetLevel(LevelDocument level, SpatialGridIndex index)`
- `LevelViewport.FrameAll()`
- `LevelViewport.SelectedFloor : int`
- `LevelViewport.LastCandidateCount : int`
- `LevelViewport.LastDrawnCount : int`

- [ ] **Step 1: Implement `LevelViewport`**

Create a single `FrameworkElement` which stores a level/index, camera center, zoom, pan state, and selected floor. Override `OnRender` and draw the background, visible path segments, floor dots, and selected-floor highlight with `DrawingContext`.

The viewport must query `SpatialGridIndex.Query(GetViewportWorldRect().Inflate(2f), candidates)` and cap overview drawing with a stride when candidate count exceeds 80,000.

- [ ] **Step 2: Port camera/input behavior**

Port the current WinForms camera formulas: mouse wheel zooms around the cursor, middle/right drag pans, left click selects the nearest floor, and `FrameAll()` fits `LevelDocument.Bounds` with 40 px padding on each side.

- [ ] **Step 3: Replace the placeholder in `MainWindow`**

Place `<local:LevelViewport x:Name="Viewport" />` in the center. Enable only the `Frame` button and bind `EditorCommands.Frame` to `Viewport.FrameAll()` through a `CommandBinding`.

- [ ] **Step 4: Load a synthetic startup level**

In `MainWindow`, create `LevelDocument.CreateSynthetic(4096)`, construct `SpatialGridIndex` from its positions, and pass both to the viewport. Update the status text to identify the minimal viewport slice.

- [ ] **Step 5: Bump version**

Set `EditorVersion.Current` to `0.0.54-prototype`.

- [ ] **Step 6: Verify locally**

Run:

```powershell
dotnet build src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj -c Release
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
# Historical WinForms verification step removed with the legacy host in roadmap #7.
dotnet run --project src\ExtremeEditor.Wpf
```

Manual checks: a synthetic path is visible; mouse wheel zooms around the cursor; middle/right drag pans; left click highlights the nearest floor; `Frame` fits the whole synthetic level; other toolbar actions remain disabled.

- [ ] **Step 7: Commit without merging**

Commit only the four implementation files above. Do not merge or delete the migration branch.
