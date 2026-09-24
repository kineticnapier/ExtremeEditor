# WPF Slice 1 Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a minimal WPF application shell beside the existing WinForms app without changing existing editor behavior.

**Architecture:** Add `src/ExtremeEditor.Wpf` as a separate `net8.0-windows` WPF executable. Keep the then-existing legacy WinForms host untouched. The WPF shell references the existing Core, Audio, and Rendering projects, uses `EditorVersion.Current` for its title, and contains only an editor-surface placeholder in this slice. The legacy host referenced by this historical plan was removed in roadmap #7.

**Tech Stack:** .NET 8, WPF, XAML, C#

**Spec:** `docs/superpowers/specs/2026-09-18-wpf-migration-design.md`

## Global Constraints

- All work remains on `feature/wpf-migration`.
- Keep the existing WinForms app usable and unchanged during this slice.
- Do not add `WindowsFormsHost` or WinForms controls to the WPF project.
- Do not port viewport, playback, editing, toolbar, or status-bar behavior in this slice.
- Each code-changing slice bumps `EditorVersion`; this slice moves `0.0.50-prototype` to `0.0.51-prototype`.
- Slice 1 is project/XAML scaffolding, so it uses the agreed scaffolding/configuration exception to TDD; verification is build plus manual launch.

---

### Task 1: Add the minimal WPF shell

**Files:**
- Create: `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`
- Create: `src/ExtremeEditor.Wpf/App.xaml`
- Create: `src/ExtremeEditor.Wpf/App.xaml.cs`
- Create: `src/ExtremeEditor.Wpf/MainWindow.xaml`
- Create: `src/ExtremeEditor.Wpf/MainWindow.xaml.cs`
- Modify: `src/ExtremeEditor.Core/EditorVersion.cs`

**Interfaces:**
- Consumes: `ExtremeEditor.Core.EditorVersion.Current`
- Produces: executable WPF project `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj` and `ExtremeEditor.Wpf.MainWindow`

- [ ] **Step 1: Create the WPF project file**

Use `Microsoft.NET.Sdk`, `OutputType=WinExe`, `TargetFramework=net8.0-windows`, and `UseWPF=true`. Reference Core, Audio, and Rendering with relative `ProjectReference` entries. Do not set `UseWindowsForms` in the WPF project.

- [ ] **Step 2: Add WPF application startup files**

Create `App.xaml` with `StartupUri="MainWindow.xaml"` and matching `App.xaml.cs` deriving from `System.Windows.Application`.

- [ ] **Step 3: Add the placeholder window**

Create a `MainWindow` with a dark `Border` placeholder filling the client area and centered text `WPF editor surface — migration in progress`. In `MainWindow.xaml.cs`, set `Title = $"ExtremeEditor {EditorVersion.Current} — WPF";` after `InitializeComponent()`.

- [ ] **Step 4: Bump the shared editor version**

Change `EditorVersion.Current` from `0.0.50-prototype` to `0.0.51-prototype`.

- [ ] **Step 5: Build the new WPF project**

Run:

```powershell
dotnet build src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj -c Release
```

Expected: exit code 0 with no compile errors.

- [ ] **Step 6: Re-run existing Core tests**

Run:

```powershell
dotnet run -c Release --project src\ExtremeEditor.Core.Tests
```

Expected: all existing Core regression tests pass.

- [ ] **Step 7: Verify the existing WinForms app still builds**

Run:

```powershell
# Historical WinForms verification step removed with the legacy host in roadmap #7.
```

Expected: exit code 0.

- [ ] **Step 8: Launch the WPF shell manually**

Run:

```powershell
dotnet run --project src\ExtremeEditor.Wpf
```

Expected: a WPF window opens, the title contains `ExtremeEditor 0.0.51-prototype — WPF`, and the center shows `WPF editor surface — migration in progress`.

- [ ] **Step 9: Commit the slice**

Commit only the six production files above after verification. Do not merge or delete the migration branch.
