# ExtremeEditor Pure WPF Migration Design

## Goal

Migrate ExtremeEditor from WinForms/GDI+ to a pure WPF application while keeping the existing WinForms application usable until the WPF path reaches functional parity.

The final application must not depend on `System.Windows.Forms`, `WindowsFormsHost`, or WinForms controls for its primary UI/rendering path.

## Branch strategy

All WPF migration work is accumulated on the single long-lived branch:

`feature/wpf-migration`

The migration is implemented as small, independently testable commits on that branch. We do not create a new feature branch for each migration slice.

## Migration strategy

Add a new WPF application project beside the existing WinForms project first. Do not rewrite the working WinForms application in place.

Intermediate repository shape:

```text
src/
  legacy WinForms host/     # historical migration source; removed in roadmap #7
  ExtremeEditor.Wpf/        # new WPF app under migration
  ExtremeEditor.Core/
  ExtremeEditor.Audio/
  ExtremeEditor.Rendering/
```

After the WPF application reaches functional parity, make it the primary application and remove the old WinForms/GDI-specific application path in a final cleanup step.

## Reuse boundaries

### Reuse unchanged where possible

- `ExtremeEditor.Core`
- `ExtremeEditor.Audio`
- `SpatialGridIndex`
- level loading/saving logic
- timing map and playback timing logic
- hit-sound timeline logic
- ADOFAI geometry/model calculations that are UI-framework independent

### Replace for WPF

- `MainForm : Form` -> `MainWindow : Window`
- `LevelCanvas : Control` -> WPF custom viewport element
- `ToolStrip` -> WPF toolbar/menu controls
- `StatusStrip` -> WPF `StatusBar`
- WinForms timer -> WPF `DispatcherTimer`
- WinForms dialogs -> WPF-compatible dialogs
- GDI+/`Graphics` render path -> WPF `DrawingContext`/`DrawingVisual` render path
- WinForms mouse/keyboard event handling -> WPF input events/commands

## Viewport architecture

Do not create one WPF visual/control per floor. Large charts can contain millions of floors, so a normal WPF item-control tree would be unusable.

Use one custom viewport surface and retain spatial culling:

```text
LevelDocument
    -> SpatialGridIndex
    -> visible floor candidates
    -> custom WPF viewport
    -> DrawingContext / DrawingVisual
```

The viewport owns camera position, zoom, panning, hit testing, floor selection, playback-follow behavior, and invalidation. It only draws visible candidates.

The migration should preserve the current large-chart behavior before attempting larger rendering redesigns.

## UI architecture

The first migration should stay pragmatic rather than immediately introducing a full MVVM framework.

- XAML owns layout and styling.
- `MainWindow` code-behind may coordinate existing Core/Audio services during migration.
- Reusable commands/state can be extracted incrementally when duplication appears.
- Avoid adding an external MVVM framework solely for this migration.

This minimizes simultaneous architectural changes while still gaining WPF layout/binding capabilities.

## Migration slices

### Slice 1: WPF shell

Create `ExtremeEditor.Wpf` with:

- `UseWPF=true`
- `App.xaml`
- `MainWindow.xaml`
- references to Core, Audio, and Rendering as needed
- window title including `EditorVersion.Current`
- empty editor surface placeholder

Success criterion: project builds and launches a WPF window without affecting the WinForms app.

### Slice 2: editor chrome

Port the visible shell:

- toolbar/buttons
- status bar
- basic grid layout
- keyboard shortcuts where they do not depend on the viewport yet

Success criterion: WPF shell exposes the same top-level actions, with actions that are not yet implemented explicitly disabled rather than silently broken.

### Slice 3: minimal viewport

Implement the WPF custom viewport with:

- synthetic/startup level
- dot/path rendering
- spatial culling
- frame-all
- pan
- zoom
- nearest-floor selection

Success criterion: large level geometry can be navigated without creating per-floor WPF controls.

### Slice 4: full floor/icon rendering

Port floor textures/meshes and event icons from GDI rendering to WPF primitives/images.

Success criterion: the WPF viewport visually covers the current WinForms floor-preview behavior closely enough for editing.

### Slice 5: level loading and playback

Port:

- `.adofai` open
- timing map construction
- audio loading
- play/pause/stop
- playback planets
- follow-player
- seek-from-selected-floor
- hit sounds

Success criterion: representative levels play with the same timing behavior as the current app.

### Slice 6: editing/save/import

Port:

- rotate selected floor
- save-as
- floor asset import
- icon catalog import
- hit-sound import
- remaining shortcuts/status diagnostics needed for normal use

Success criterion: normal editing workflows no longer require the WinForms app.

### Slice 7: cutover and cleanup

After parity verification:

- make WPF the primary app
- remove the old WinForms application project/path
- remove `System.Windows.Forms` dependencies from the application UI
- remove obsolete GDI-only renderer code if no longer referenced
- update docs/build commands

Success criterion: the normal application is pure WPF and all retained tests/builds pass.

## Testing and verification

Each code-changing slice must bump `EditorVersion`.

For each slice:

- build the affected project(s)
- run existing Core tests
- run existing Audio tests when playback/audio behavior is touched
- add focused tests for framework-independent behavior extracted during the migration
- manually launch the WPF app for UI/rendering verification when required

Do not claim a UI slice is complete from compilation alone; launch/runtime evidence is required for interactive behavior.

## Non-goals during migration

- replacing the audio engine
- rewriting timing logic
- changing ADOFAI semantics
- introducing a third-party UI framework
- changing the giant-chart culling strategy without evidence that WPF requires it
- redesigning the entire editor UX at the same time as framework migration

The migration should preserve behavior first. UI redesign can follow after the WPF cutover.
