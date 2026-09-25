# Documentation status

The repository root `README.md` is the source of truth for current startup,
architecture, asset setup, build/test commands, and roadmap status.

`docs/superpowers/specs` and `docs/superpowers/plans` are retained as
historical design and implementation snapshots. They document why earlier WPF,
temporal/raster, and native-renderer decisions were made and preserve useful
regression rationale. They are not active task lists:

- unchecked boxes do not indicate pending current work;
- branch, commit, version, and command instructions describe the original slice;
- references to WinForms or a WPF `LevelViewport` production path describe
  superseded states;
- the legacy WinForms host was removed in roadmap #7;
- production now uses `NativeLevelViewport` and
  `ExtremeEditor.NativeRenderer.dll`;
- the WPF `LevelViewport` path remains only for comparison and regression use.

Consult current code and the root README before reusing a historical design.
