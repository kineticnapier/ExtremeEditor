# Consolidated Issue Fix Batch

This branch is the single working branch for accumulating verified fixes to open issues in ExtremeEditor.

## Current integration cycle

- Consolidated batch PR #112 was integrated into `feature/segment-model-poc` on 2026-10-30.
- Integration PR #119 merged the verified prototype state into `main` on 2026-10-30.
- Subsequent issue fixes continue through a new draft PR targeting `main`; no fix is claimed by this tracking-only update.

## Workflow

- Before each change, check open/closed issues and existing pull requests to avoid duplicate work.
- Implement fixes on this branch and add focused regression tests.
- Reference each addressed issue in the consolidated pull request and relevant commit messages.
- Do not claim an issue fixed until code and tests support it.
- Do not automatically merge the pull request.
- Preserve the repository's existing behavior and compatibility except for explicitly documented fixes.

## Implemented issues

- #111 — Standard WPF test entry now invokes 10 existing video export/background suites; corrected the streaming encoder contract to allow the required atomic MP4 temporary path. **Implemented; CI verified** (GitHub Actions run 40680819674: WPF/Core/Audio/native tests all passed on Windows). Focused-only environment-variable run not separately executed.

- #113 — Reload the native level image table when an active CustomBackground bgImage changes, then re-upload the playback timeline. Added resolver and source-wiring regressions. **Implemented; Windows CI passed (run 40689134587); visual GUI verification pending**.

- #110 — Replaced the first-two-channel-only extraction with a layout-unknown fold-down that retains channels 3+ in stereo, plus 3ch/6ch/mono/stereo regression tests in the default Audio suite. **Implemented; Windows CI passed** (final code HEAD `2313b336`, run 40698026422). Speaker-layout-aware 5.1 positioning, real audio checks, and listening tests remain unverified; this is an interim fallback, not a fully validated surround downmix.

- #109 — Headless video renderer now uploads background image assets and CustomBackground timeline just like the interactive viewport. Added a source-wiring regression to the existing default WPF CustomBackground suite. **Implemented; Windows CI passed** (final code HEAD `2313b336`, run 40698026422); **video pixel comparison pending**. No claim of real MP4 rendering parity yet.

- #56 — Full local managed regression runner now includes WPF tests alongside Core/Audio, while Quick stays Core/Audio only. CI now executes `./test-all.ps1` so omission of WPF or nonzero test exits fail the Windows job. **Implemented; Windows CI passed** for code HEAD `6726463` (run 40708224345). Manual PowerShell `-NoBuild` / `-Native` variations and a deliberately failing WPF test have not been exercised.

- #92 — Removed unconditional Alt+Space/SC_KEYMENU interception from the native WPF keyboard bridge; added a default and UI-focused source-wiring regression. **Implemented; Windows CI passed (run 40717516564); manual system-menu keyboard interaction pending**. Commits `3c8f3fe`, `8a9f442`, `efc9985`.

- #101 — SetSpeed and other enum editors now match known values case-insensitively and show unknown values without rewriting the JSON; added and registered unit regression cases. **Implemented; Windows CI passed (run 40717516564); GUI/round-trip validation pending**. Commits `f5a27ad`, `559d329`, `778ca92`.

- #76 — Aligned Ctrl+F native visible floor labels and WPF selection summaries to the existing one-based Ctrl+G floor numbering. Extended WPF GoToFloor boundary tests and native source parity contract. **Implemented; Windows CI passed (run 40728741786); real GUI verification pending**. Commits `f63c231`, `33dacd2`, `90659b0`, `071f7de`.

- #86 — Song Volume-only edits update the live audio gain without stopping or resetting playback. Other song settings retain their existing stop/rebuild behavior. Added classification and source-order regression to SongSettingsSliceRegression and registered it in the default WPF suite. **Implemented; Windows CI passed (run 40738138635); actual audio/UI playback verification pending**. Commits `bb36e12`, `4e4687e`, `f458851`.

- #108 — Skip parsing a Raw JSON draft whose SourceIndex no longer exists during Save; keep it until Save so Undo-before-save still validates restored actions. Added malformed-orphan, Undo-restored-invalid, and valid-restored regression cases in the default WPF RawEventDraftSafetyRegression. **Implemented; Windows CI passed (run 40738138635); GUI save/Undo verification pending**. Commits `b59cfb6`, `30f4fef`.

- #107 — Video export now checks tab-level dirty state (including pending Raw JSON drafts) and saves before reloading the chart from disk; aborts if Save fails, switches tabs, or leaves newer edits. Added clean/draft/normal-edit/untitled/empty-path regressions and source wiring checks. **Implemented; Windows CI passed (run 40756320151); actual video export verification pending**. Commits `28cec62`, `291b642`.

- #78 — Ctrl+Z/Ctrl+Y/Ctrl+Shift+Z now dispatches ahead of the empty-floor-selection guard while preserving the editor-focus filter. Added native/text focus, empty-selection Undo/Redo, key-chord and source-order regression tests. **Implemented; Windows CI passed (run 40756320151); native HWND keyboard validation pending**. Commits `99fd43f`, `7410b10`, `8c4fcce`, `4e87007`.

- #72 — Floor-zero-inclusive multi-selection now deletes only editable selected floors instead of aborting, and both native Ctrl+C and menu Copy report the actual clipboard count. Added floor-zero, mixed-selection deletion, copy-count and Undo regression to the existing default WPF clipboard suite. **Implemented; Windows CI and GUI verification pending**. Commits `82e237c`, `041217f`, `93cba5e`.

- #79 — Added explicit Edit > Paste at End (RoutedUICommand), while Ctrl+V retains the existing before-selected-floor behavior. EditorSession.PasteFloors accepts an opt-in `allowEnd` flag and supports inserting after the last floor. Extended EditorFloorClipboardRegression for final-floor insertion, action/decorations, Undo/Redo and legacy boundary behavior. **Implemented; Windows CI and GUI validation pending.** Commits `4d82eec`, `bb45ca0`, `c4b941c`, `c392886`, `1b2868c`.

- #88 — When a referenced song exists but decoding fails, both initial chart loading and Song Filename changes now try the hitsound-only playback graph. Decoder, fallback and cleanup errors remain visible; added `SongAudioFallbackRegression` to default/focused WPF tests. **Implemented; Windows CI passed at `228bd85` (run 40830660324); actual corrupt-song playback and WaveOut cleanup still unverified.** Draft PR #112, `Refs #88` (not `Fixes`).

- #51 — Closing the final tab now waits for asynchronous creation and activation of a replacement before removing the old tab. Failed/superseded replacement activation cleans up the temporary tab; concurrent close requests are guarded. Extended `DocumentTabCloseRegression` for delayed activation, failure, and same-tab rejection. **Implemented; Windows CI and real GUI tests pending.** Commits `477f525`, `ee4dcae`, `1511677`, `9ac946f`. Draft PR #112, `Refs #51` (not `Fixes`).

- #75 — Idle/paused native playback anchors are deduplicated, while playing anchors and state transitions still upload. Added `NativePlaybackAnchorDispatchRegression` to default/focused WPF tests. **Partial implementation; Windows CI passed at `b20b539` (run 40843751910); 16ms WPF timer and decoration evaluation remain, CPU/GUI tests pending.** Draft PR #112, `Refs #75` (not `Fixes`).

- #53 — Width-constrained tab strip with fixed New Tab and left/right scroll controls, mouse-wheel navigation, and active-tab auto-reveal on selection/resize. Added `DocumentTabOverflowRegression` to default/focused WPF tests. **Implemented; Windows CI pending at `ad46d63` (run 40850897184); real WPF layout and accessibility verification pending.** Draft PR #112, `Refs #53` (not `Fixes`).
