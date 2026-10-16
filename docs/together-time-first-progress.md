# ExtremeEditor Together — Time-first completion progress

Updated: 2026-10-16 (Asia/Tokyo)

## Current phase

Phase D — Time-first workspace persistence (next RED).

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

## Last verified implementation commit

`fca8c4bf690280e26fa4423df94811e6e4524c7b`

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

Phase A RED was `0c6ce75`. Phase B RED was `b598a6b`. Phase C RED was
`3575adc`; its WPF test failed for the expected `AudioWaveformLoader.Load`
`NotImplementedException` after a successful Release build.

## Unresolved

- Time-first draft persistence, real tile authoring, Git integration,
  semantic composition, and safe export.
- Final manual GUI interaction review remains required; GUI automation is
  prohibited.

## Next concrete work

Add a versioned Phase D workspace schema that stores the audio identity,
musical timing points, time allocations, explicit empty content, and draft
anchors. It must roundtrip all-empty projects, reject missing files distinctly
from empty content, retain legacy floor-aligned workspaces unchanged, and wire
the WPF planned ranges into Create/Open.
