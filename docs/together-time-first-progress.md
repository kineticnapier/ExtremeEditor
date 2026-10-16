# ExtremeEditor Together — Time-first completion progress

Updated: 2026-10-16 (Asia/Tokyo)

## Current phase

Phase E — Time-first chart authoring (next RED).

## Completed

- Phase A: constant-BPM music beat grid on the shared integer audio-sample
  axis.
- Supports `1/1`, `1/2`, `1/3`, `1/4`, `1/8`, and `1/16` subdivisions.
- Direct rational calculation from the beat origin avoids cumulative drift.
- Nearest-line snapping has deterministic midpoint behavior.
- Visible-range generation is half-open, immutable, and explicitly bounded.
- The music grid remains separate from ADOFAI floor/`SetSpeed`/`Pause` timing.
- Phase B: immutable, strictly ordered musical timing points with BPM changes.
- Timing sections are half-open; a cut is emitted once as the new section's
  whole-beat origin, and snapping rejects old-BPM lines beyond the cut.
- Multi-section visible generation retains one total line budget and copies
  caller-owned timing-point data.
- Phase C: real audio waveform decoding with measured sample rate and a bounded
  peak summary.
- WPF music timeline paints visible waveform/grid/Segment bands without
  per-sample controls and supports drag selection, six snap values, zoom, and
  horizontal scrolling.
- The Japanese Together panel can load an audio file, configure BPM/beat
  offset, and plan owned empty sample-time Segments without existing floors.
- Phase D: a versioned, isolated time-first Git workspace persists measured
  audio identity, musical timing points, sample-time ownership, explicit empty
  content, and immutable per-Segment drafts.
- Missing Segment payloads are diagnosed separately from legitimate empty
  Segments; caller collections are frozen on reload.
- The WPF create/open flow now stores and restores planned time Segments while
  leaving legacy floor-aligned workspaces unchanged.

## Last verified implementation commit

`2ac6a396c6d55e0a4ba3e5e30df47111bb7dffbd`

## Windows verification

- `dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release`
  — exit 0, 0 warnings, 0 errors.
- `EXTREMEEDITOR_MUSIC_GRID_ONLY=1` — exit 0.
- `EXTREMEEDITOR_MUSIC_TIMING_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_SEARCH_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_DRAFT_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_EMPTY_ONLY=1` — exit 0.
- WPF Release build with `--no-restore -m:1` — exit 0, 0 warnings, 0 errors.
- `EXTREMEEDITOR_TOGETHER_TIMELINE_ONLY=1` — exit 0.
- `EXTREMEEDITOR_TOGETHER_JA_UI_ONLY=1` — exit 0.
- `EXTREMEEDITOR_TOGETHER_WPF_ONLY=1` — exit 0.
- `EXTREMEEDITOR_TIME_WORKSPACE_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_GIT_ONLY=1` — exit 0.

Phase A RED was `0c6ce75`. Phase B RED was `b598a6b`. Phase C RED was
`3575adc`; its WPF test failed for the expected `AudioWaveformLoader.Load`
`NotImplementedException` after a successful Release build. Phase D RED was
`75a8a4c`; its dedicated test failed for the expected
`TimeFirstSegmentGitWorkspace.Initialize` `NotImplementedException` after a
successful Release build.

## Unresolved

- Real tile authoring, time-first Git synchronization, semantic composition,
  and safe export.
- Final manual GUI interaction review remains required; GUI automation is
  prohibited.

## Next concrete work

Define a Phase E authored-tile representation that resolves each draft marker
to real ADOFAI geometry and events, preserves entry/exit musical state, supports
Undo/Redo and save/reopen, and is editable only by the active Segment owner.
