# WPF Slice 2 Editor Chrome Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recreate the visible editor chrome in WPF while keeping every not-yet-migrated action explicitly disabled.

**Architecture:** Keep `MainWindow` as a lightweight WPF shell. XAML owns the toolbar, central placeholder, and status bar. A small `EditorCommands` class defines the keyboard gestures now so later slices can attach handlers without changing the gesture contract.

**Tech Stack:** .NET 8, WPF, XAML, C#

**Spec:** `docs/superpowers/specs/2026-09-18-wpf-migration-design.md`

## Global Constraints

- Continue on `feature/wpf-migration`.
- Keep `ExtremeEditor.App` unchanged.
- Do not introduce WinForms interop or `WindowsFormsHost`.
- Do not implement file loading, viewport logic, playback, editing, saving, imports, or benchmarking yet.
- Actions without migrated implementations must be visibly disabled.
- Bump `EditorVersion` from `0.0.51-prototype` to `0.0.52-prototype`.

---

### Task 1: Add command contracts for the existing shortcuts

**Files:**
- Create: `src/ExtremeEditor.Wpf/EditorCommands.cs`

**Produces:** `RoutedUICommand` definitions for Open, Frame, Play/Pause, Stop, Save As, Rotate Left, and Rotate Right.

- [ ] Define Ctrl+O, F, Space, Escape, Ctrl+S, `[`, and `]` gestures.
- [ ] Do not add command handlers in this slice; unresolved routed commands therefore remain disabled.

### Task 2: Build the WPF editor chrome

**Files:**
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml`
- Modify: `src/ExtremeEditor.Wpf/MainWindow.xaml.cs`

**Produces:** Toolbar, central placeholder, status bar, and version status text.

- [ ] Replace the single placeholder grid with a `DockPanel`.
- [ ] Add toolbar entries matching the current WinForms shell: Open, Frame, Play, Stop, Follow Player, playback time, -15°, +15°, Save As, Import Probe Assets, Import Icon Catalog, Import Hitsounds, Floor Preview, and Benchmark viewport.
- [ ] Bind the command-backed buttons to `EditorCommands`; leave all other not-yet-migrated controls with `IsEnabled="False"`.
- [ ] Keep the central `WPF editor surface — migration in progress` placeholder.
- [ ] Add a bottom `StatusBar` with a named text element populated from `EditorVersion.Current`.

### Task 3: Bump version and verify

**Files:**
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

- [ ] Change `EditorVersion.Current` to `0.0.52-prototype`.
- [ ] Run `dotnet build src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj -c Release`.
- [ ] Run `dotnet run -c Release --project src\ExtremeEditor.Core.Tests`.
- [ ] Run `dotnet build src\ExtremeEditor.App\ExtremeEditor.App.csproj -c Release`.
- [ ] Launch `dotnet run --project src\ExtremeEditor.Wpf` and verify the toolbar, disabled actions, center placeholder, and status bar render correctly.
