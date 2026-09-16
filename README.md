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

`ExtremeEditor.Core` has no UI or game dependency. `ExtremeEditor.Rendering` owns floor-preview geometry, the local asset cache and the current GDI+ preview backend. `ExtremeEditor.App` is the WinForms host. The rendering boundary is intentionally separate so a batched GPU backend can replace GDI+ without rewriting the level model, parser or spatial index.

`ExtremeEditor.Audio` owns one streaming audio graph and one output device for the song and hit sounds. Hit sounds are converted from floor entry times to absolute sample frames and mixed during each provider read; the graph keeps only a sparse state timeline, a floor cursor, and currently audible PCM tails rather than materializing one audio object or a whole-song PCM buffer per floor.

The prototype follows ADOFAI's `(-angle + 90°)` path-direction convention for ordinary `angleData` values. It is not yet a byte-for-byte behavioral replacement for ADOFAI's editor. In particular, full event semantics, legacy `pathData`, midspins, twirls, pauses, multi-planet behavior and decoration/VFX preview need dedicated compatibility work.

## Audio foundation checks

```powershell
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

The dependency-free check executable verifies chart/audio clock transforms, 48 kHz sample mapping, seek tail reconstruction, chunk-boundary tails, sparse `SetHitsound` state, and a streamed one-million-floor cue scan without opening an audio device.

## 0.0.19 prototype

Makes streamed hit scheduling robust when adjacent hit-sound types have different manifest offsets. The renderer now stops floor scanning only after applying the maximum positive offset bound, and preserves the audible tail of clips whose start frame is before audio time zero.

## 0.0.18 prototype

Moves song and hit-sound playback onto a single `WaveOutEvent` graph. Hit-sound PCM is placed at absolute sample frames inside the audio provider, removing WinForms Timer/Paint scheduling, lead/late windows, and the per-frame hit cap from `MainForm`.

## 0.0.6 prototype

Adds the first ADOFAI-like floor preview based on runtime data exported by the EditorQoL Asset Probe:

- stores the observed standard straight-floor mesh as 16 vertices / 36 indices, including UV and UV2 data;
- adds a separate `ExtremeEditor.Rendering` project;
- renders the observed main floor and top/bottom shadow geometry at editing zoom;
- falls back to the old sampled-dot overview when zoomed out or when too many floors are visible;
- adds **Import Probe Assets**, which recognizes `_TileTex`, `_PerlinTex`, `_MainTex` and `light_white` PNGs from an EditorQoL `*-assets` folder;
- copies those user-extracted files into `%LocalAppData%\ExtremeEditor\AssetCache\floor-mesh` instead of committing game assets to this repository;
- uses the imported tile texture in the current preview and loads the remaining textures for later shader-parity work.

This is intentionally an interim renderer. It currently rotates the observed straight-floor mesh along the outgoing path direction; exact curved/corner floor geometry and an `ADOFAI/FloorMesh`-equivalent shader are not implemented yet. The current GDI+ backend also switches back to the lightweight overview above 18,000 visible floors; the intended production backend is batched/GPU rendering.

## 0.0.4 prototype

Fixes the ADOFAI world-vector convention. `scrLevelMaker` computes
`exitAngle = -angle + 90°`, but `scrMisc.getVectorFromAngle` uses
`(sin(angle), cos(angle))`, not the conventional `(cos(angle), sin(angle))`.
0.0.3 accidentally swapped those components, collapsing charts such as the
238k-floor test chart into the wrong orientation.
