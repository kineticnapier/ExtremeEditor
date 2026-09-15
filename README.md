# ExtremeEditor

Standalone proof-of-concept ADOFAI editor for extremely large levels.

This prototype deliberately has **no Unity / UMM / Harmony / Workbench dependency**.

## Prototype goals

- Open a normal `.adofai` file directly.
- Keep the level model as plain C# arrays/structs.
- Build floor positions once.
- Build a spatial grid once.
- Draw only floors intersecting the current viewport.
- Pan, zoom and select floors without creating one UI/GameObject per floor.
- Rotate a selected floor by ±15°, rebuild geometry/index, and Save As while preserving the rest of the JSON.
- Show load/index/render timing and visible/candidate floor counts.
- Provide a headless benchmark for large files.

The first target is the 200k-angle / 200k-SetSpeed class of level. Event editing, exact ADOFAI timing semantics, decorations, audio and asset extraction are intentionally not implemented yet. Angle editing is deliberately minimal so the prototype can exercise the full load → select → mutate → rebuild → save path.

## Requirements

- Windows
- .NET 8 SDK

## Run

```powershell
dotnet run --project src/ExtremeEditor.App
```

or:

```powershell
.\run.ps1
```

Open an `.adofai` from the toolbar. You can also click **Synthetic 238k** to generate a large in-memory path without a file.

Controls:

- Mouse wheel: zoom around cursor
- Middle drag or right drag: pan
- Left click: select nearest visible floor
- `F`: frame entire level
- `O`: open file
- `[` / `]`: rotate selected floor by -15° / +15°
- `Ctrl+S`: Save As
- `Esc`: clear selection

## Benchmark

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

The benchmark reports JSON parse, path construction, spatial-index construction and viewport-query timings.

## Architecture

`ExtremeEditor.Core` has no UI or game dependency. `ExtremeEditor.App` is currently a minimal WinForms host using one custom-drawn surface. A future renderer can replace WinForms without rewriting the level model, parser or spatial index.

The prototype follows ADOFAI's `(-angle + 90°)` path-direction convention for ordinary `angleData` values. It is not yet a byte-for-byte behavioral replacement for ADOFAI's editor. In particular, full event semantics, legacy `pathData`, midspins, twirls, pauses, multi-planet behavior and decoration/VFX preview need dedicated compatibility work.

## 0.0.4 prototype

Fixes the ADOFAI world-vector convention. `scrLevelMaker` computes
`exitAngle = -angle + 90°`, but `scrMisc.getVectorFromAngle` uses
`(sin(angle), cos(angle))`, not the conventional `(cos(angle), sin(angle))`.
0.0.3 accidentally swapped those components, collapsing charts such as the
238k-floor test chart into the wrong orientation.
