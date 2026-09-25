# ExtremeEditor

ExtremeEditor is a standalone Windows editor for very large A Dance of Fire and Ice
(`.adofai`) levels. The production application is WPF with a native C++ viewport;
it has no Unity, UMM, Harmony, or Workbench runtime dependency.

The current version is `0.0.305-prototype`. `feature/wpf-migration` is the
default development branch; despite its historical name, the WinForms-to-WPF
migration and removal of the legacy WinForms host are complete.

## Requirements

- Windows
- .NET 8 SDK
- CMake
- Visual Studio C++ build tools with a Windows SDK and x64 support
- A legally installed copy of ADOFAI when extracting game assets

## Run

The standard startup command is:

```powershell
.\run.ps1
```

`run.ps1` launches
`src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`. To run a Release build
directly:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf
```

Pass an optional level path after `--`:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

Without a supplied file, the editor opens a small synthetic level. Use the Open
command to load an `.adofai` file.

Common controls:

- Mouse wheel: zoom around the pointer
- Middle or right drag: pan
- Left click: select a floor
- `F`: frame the level
- `O`: open a level
- `[` / `]`: rotate the primary selected floor by -15° / +15°
- `Ctrl+S`: save
- `Esc`: clear selection

## Production architecture

```text
ExtremeEditor.Wpf (C#)
├─ MainWindow / toolbar / inspector
├─ LevelDocument / TimingMap
├─ editor commands / undo / redo
├─ save / load
├─ audio
├─ EditorSelectionState
└─ NativeLevelViewport : HwndHost
   │
   └─ P/Invoke
      ↓
ExtremeEditor.NativeRenderer.dll (C++)
├─ child HWND
├─ render thread
├─ D3D11 / DXGI / Direct2D
├─ camera / pan / zoom
├─ floor / icon / planet / selection drawing
├─ hit testing
└─ native viewport input
```

C# is authoritative for editing semantics, the level document, timing, undo/redo,
save/load, audio, and selection. C++ owns production viewport rendering, culling,
hit testing, camera interaction, and native viewport input. Immutable native
snapshots cross a flat P/Invoke boundary.

`src/ExtremeEditor.Wpf/LevelViewport*.cs` is intentionally retained for
comparison and regression coverage of the older WPF temporal/raster rendering
path. `WpfPlaybackPresenter`, `WpfFloorRenderer`, `WpfIconRenderer`,
`WpfLevelLoader`, raster-cache classes, and `SpatialGridIndex` support those
tests and shared non-production responsibilities. They are not a second
production viewport: `MainWindow` hosts only `NativeLevelViewport`.

The removed WinForms host is historical and is not part of startup, build, or
production rendering.

## Asset setup

The WPF app uses the standalone `ExtremeEditor.AssetExtractor.exe`. The WPF
project does not reference the extractor library; its build produces and copies
the executable, and asset setup starts it as a subprocess.

Asset setup:

1. Checks the persisted ADOFAI installation path.
2. Searches Steam libraries and the ADOFAI app manifest.
3. Offers a browse fallback when automatic discovery fails.
4. Persists the selected valid game path.
5. Extracts assets into
   `%LocalAppData%\ExtremeEditor\AssetCache`.

The old AssetProbe/manual-import workflow is retired and is not a supported setup
path.

## Build and test

Build the production application:

```powershell
dotnet build src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj -c Release --nologo
```

Run managed regression suites:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf.Tests
dotnet run -c Release --project src/ExtremeEditor.Core.Tests
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

When `build/native-renderer` has been generated, run the native suite with:

```powershell
ctest --test-dir build/native-renderer -C Release --output-on-failure
```

The WPF regression suite includes contracts that prevent restoration of the
legacy WinForms host and prevent the WPF comparison viewport from returning to
the production `MainWindow`.

## Benchmark and diagnostics

Run the headless Core benchmark:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

To record first-load and audio diagnostics:

```powershell
$env:EXTREMEEDITOR_DIAGNOSTICS = "$PWD\first-load.log"
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

Use `EXTREMEEDITOR_DIAGNOSTICS=1` to write the default diagnostic log in the
temporary directory. Diagnostics are disabled by default.

## Compatibility and performance policy

- Target at least 60 FPS in the production viewport.
- Nearby tiles must retain their ADOFAI-style appearance, including on dense
  charts; replacing them with dots is not an acceptable optimization.
- Preserve audio synchronization accuracy.
- Treat behavior observed in the ADOFAI DLL/decompiled implementation as the
  compatibility baseline.
- Keep large-chart work proportional to the visible or temporally relevant set
  wherever practical.

ExtremeEditor is not yet a byte-for-byte replacement for the stock editor.
Compatibility work should be backed by focused regression tests.

## Roadmap

Completed:

- Native-only production viewport
- Removal of the legacy WinForms host
- README and migration-document cleanup
- UI cleanup and diagnostics decluttering

Next:

1. Sample-accurate hit-sound pre-render
2. Consolidate the production song + hit-sound unified audio graph

Detailed documents under `docs/superpowers` are historical design and
implementation snapshots. See `docs/README.md` before using them; their
checklists and commands are not the current roadmap.
