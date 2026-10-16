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


## 現在進行中の共同編集設計（重要）

- **[Together: 曲の時間区間を先に作る設計](together-time-first-design.md)** — 2026-10-16 に合意した目標仕様。osu!風BPM／オフセットグリッド、空セグメント、担当割当、時間検索の二分探索条件、結合検証を記録。**現行の床分割PoCはこの最終仕様をまだ満たしていません。**

この文書は今後のTogether Core/UI設計で優先して参照する。PoCの実装状態はそれぞれの回帰記録と区別する。
