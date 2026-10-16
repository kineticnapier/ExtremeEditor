# ExtremeEditor Together — Time-first completion progress

Updated: 2026-10-16 (Asia/Tokyo)

## Current phase

Phase B — Musical Timing Points (next RED).

## Completed

- Phase A: constant-BPM music beat grid on the shared integer audio-sample
  axis.
- Supports `1/1`, `1/2`, `1/3`, `1/4`, `1/8`, and `1/16` subdivisions.
- Direct rational calculation from the beat origin avoids cumulative drift.
- Nearest-line snapping has deterministic midpoint behavior.
- Visible-range generation is half-open, immutable, and explicitly bounded.
- The music grid remains separate from ADOFAI floor/`SetSpeed`/`Pause` timing.

## Last verified implementation commit

`ee8a864edce301b6d74750cabed85808641ee3e3`

## Windows verification

- `dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release`
  — exit 0, 0 warnings, 0 errors.
- `EXTREMEEDITOR_MUSIC_GRID_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_SEARCH_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_DRAFT_ONLY=1` — exit 0.
- `EXTREMEEDITOR_SEGMENT_EMPTY_ONLY=1` — exit 0.

The preceding RED commit was `0c6ce75`; its dedicated test failed for the
expected `MusicBeatGrid.CreateConstant` `NotImplementedException` after a
successful Release build.

## Unresolved

- Multiple BPM timing points and continuity across their boundaries.
- WPF waveform/grid timeline and time-range ownership creation.
- Time-first draft persistence, real tile authoring, Git integration,
  semantic composition, and safe export.
- Final manual GUI interaction review remains required; GUI automation is
  prohibited.

## Next concrete work

Add a Phase B RED for an immutable timing-point map that validates ordered
sample boundaries, preserves beat phase across BPM changes, snaps at section
cuts without duplicate grid lines, and generates only visible lines.
