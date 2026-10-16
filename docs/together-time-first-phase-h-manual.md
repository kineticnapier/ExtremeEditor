# Together — Phase H Windows manual integration / E2E checklist

EditorVersion: `0.0.602-prototype`. Branch: `feature/segment-model-poc`.

**Scope:** The time-first UI now calls the Phase F local-bare Git transport
and Phase G conservative `.adofai` publication API. This document is a
**manual Windows test plan**, not a claim that WPF was exercised on Linux or
that arbitrary output is guaranteed playable in the stock game. GUI automation
(SendKeys, Alt+Tab, scripted clicking) is prohibited.

## Build and focused regression (run in PowerShell from repository root)

```powershell
$ErrorActionPreference = 'Stop'
dotnet build src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release
$env:EXTREMEEDITOR_TIME_H_E2E_ONLY = '1'
dotnet run --project src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release --no-build
Remove-Item Env:EXTREMEEDITOR_TIME_H_E2E_ONLY
$env:EXTREMEEDITOR_TIME_GIT_ONLY = '1'
dotnet run --project src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release --no-build
Remove-Item Env:EXTREMEEDITOR_TIME_GIT_ONLY
$env:EXTREMEEDITOR_TIME_EXPORT_ONLY = '1'
dotnet run --project src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release --no-build
Remove-Item Env:EXTREMEEDITOR_TIME_EXPORT_ONLY
# WPF native dependencies require Windows.
dotnet build src/ExtremeEditor.Wpf.Tests/ExtremeEditor.Wpf.Tests.csproj -c Release -m:1
$env:EXTREMEEDITOR_TOGETHER_TIME_E2E_ONLY = '1'
dotnet run --project src/ExtremeEditor.Wpf.Tests/ExtremeEditor.Wpf.Tests.csproj -c Release --no-build
Remove-Item Env:EXTREMEEDITOR_TOGETHER_TIME_E2E_ONLY
```

Also rerun existing `EXTREMEEDITOR_TOGETHER_TIMELINE_ONLY=1`,
`EXTREMEEDITOR_TOGETHER_WPF_ONLY=1`, and
`EXTREMEEDITOR_TOGETHER_JA_UI_ONLY=1` WPF regression selectors.

## Together audio preview manual check

- Select an OGG/WAV/MP3 source and confirm it loads without blocking the UI.
- Click **再生**, then **一時停止**, and **停止**. The audible song and
  millisecond counter should agree and Stop should reset to the beginning.
- Click the waveform to seek to the **un-snapped** sample, including while
  playing. The orange playhead should follow the device playback clock.
- Ordinary wheel scrolls horizontally (down moves later, up earlier);
  **Ctrl+wheel** zooms around the pointer. The zoom slider must update without
  shifting the viewport unexpectedly.
- **Shift+click** places the start marker, **Alt+click** places the end marker,
  both beat-snapped. Alternatively, seek first, then use **現在位置→始点** and
  **現在位置→終点**. Either marker can be repositioned alone. The range remains
  visible even after releasing the mouse; end must exceed start to allocate.
- Drag selection remains compatible with the old workflow; plain click must
  only seek without creating an interval. Toggle **選択区間をループ**, play, and
  verify that playback returns to the beginning of the marked range.
- Zoom and scroll during playback. The playhead should track the projected
  position without repainting the full waveform at audio-clock frequency.
- Close Together while playing: audio must stop and release the device.
- Without an audio output device, waveform editing should remain possible
  and the preview should report that playback is unavailable.

## Two independent clients (manual mouse and keyboard only)

1. Prepare a **new local bare** Git remote, e.g.
   `git init --bare --initial-branch=main F:\Together-Test\shared.git`.
   This is a local transport, not a public GitHub invite URL.
2. Start client **A** with a measured audio file. Select the **entire** decoded
   song duration with contiguous assigned time intervals. Allocate segment A
   to `alice` and segment B to `bob`. Create a **new time-first workspace** in
   A's dedicated directory. Verify empty intervals are retained and floor
   counts are not fabricated.
3. Expand **詳細設定**, enter the bare repository path, click **同期先を接続**,
   **送信（Push）**. Verify a normal Git branch appears in the bare repository.
4. Start independent client **B**, select **共同編集に参加**; enter a new clone
   directory, `bob` as owner, the *same* bare path under 詳細設定 and click
   **Git共有先から複製して参加**. No existing `.adofai` template is needed for B.
5. On A, select segment A, load the **identical** audio file (matching name,
   decoded sample rate/count and SHA-256), specify entry orientation and place
   actual destination tiles. Check Undo/Redo and Stage → Commit → Push.
   On B, select B and do the analogous authoring. Attempt editing the other
   owner's segment: only read-only selection should be available.
6. Before B pulls, try changing the selected segment while uncommitted tile
   edits are in the editor: the selection must remain on B and the undo stack
   and unsaved tiles must remain. Stage/Commit, then **Fetch/Pull**. When the
   two authors changed different segments, a semantic two-parent merge should
   keep both revisions. Use Push from B, then Pull on A.
7. Try two independent edits to **the same segment** from identical ancestors
   in separate workspaces and Pull after the other commit has been pushed:
   report explicit conflicting IDs, do not move local HEAD or discard either
   revision. Non-fast-forward Push must fail without force or reset.
8. Try Pull when a time-first local editor has unsaved tile edits, and when it
   has staged-but-uncommitted files: both must refuse without data loss.
9. For a **complete compatible** chart, open 詳細設定 and enter **explicit
   ADOFAI playback BPM, Offset (ms), Pitch (%), Countdown and SeparateCountdown**.
   These are *not* inferred from the osu-like musical beat grid. Preview
   validation runs *before* selecting an export destination.
10. Choose a destination `.adofai` **outside the Git workspace** and make sure
    the exactly matching original audio file is **in the destination directory**
    with its original filename. Export checks its SHA-256 and rejects missing
    or changed audio. On a fully compatible chart, reload the exported file in
    ExtremeEditor, then open it in stock ADOFAI to inspect actual gameplay.
11. Negative test: leave one active segment empty, introduce a song-sample
    mismatch, or use an unsupported global effect. Export must explicitly
    refuse; no stale successful output may be overwritten or fabricated.

## Known restrictions / do not report as completed

- The editor exposes an **entry angle and destination angles**, but does not
  auto-fit arbitrary authored audio sample anchors to ADOFAI floor timing.
  Many hand-authored patterns will be rejected by G until manually aligned
  with geometry, offset and BPM. A failed preflight is expected and safe.
- Complex cross-segment effects, implicit tempo changes, advanced decorative
  actions and remote URLs are out of scope. Only audited effect types are
  publishable. The branch remains a **prototype**.
- Time-first authoring uses the dedicated timeline tile list, not the main
  WPF floor canvas. Do not silently claim the main canvas is live-linked.
- The Windows commands and hands-on GUI/gameplay steps above are **not yet
  independently run by this document**. Record individual outcomes before
  declaring Phase H fully verified.
