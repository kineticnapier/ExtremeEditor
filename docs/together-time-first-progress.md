# ExtremeEditor Together — Time-first completion progress

Updated: 2026-10-16 (Asia/Tokyo)

## Current phase

Phase H — Windows integration and final E2E review (after applying F/G patch series).

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
- Phase E: owner-scoped time-first authoring stores real destination angles and
  local ADOFAI actions separately from sample anchors.
- The immutable editor session supports place/remove/action edits and Undo/Redo,
  then atomically promotes the draft, content entry, and allocation revision.
- WPF users can open their own time Segment, place/remove real tiles at snapped
  samples, Undo/Redo, Stage, Commit, and reopen the same authored content;
  another owner's Segment remains read-only.

## Last verified implementation commit

Previous Windows-verified upstream: `bae1ee235b19e8a119ec40f2422152602baf3741`.
F/G implementation is provided as an offline three-commit patch series and
has not been pushed or Windows-verified as part of this handoff.

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
- `EXTREMEEDITOR_TIME_AUTHORING_ONLY=1` — exit 0.

Phase A RED was `0c6ce75`. Phase B RED was `b598a6b`. Phase C RED was
`3575adc`; its WPF test failed for the expected `AudioWaveformLoader.Load`
`NotImplementedException` after a successful Release build. Phase D RED was
`75a8a4c`; its dedicated test failed for the expected
`TimeFirstSegmentGitWorkspace.Initialize` `NotImplementedException` after a
successful Release build.
Phase E RED was `7e7cd35`; its dedicated test failed for the expected
`TimeFirstSegmentEditorSession.Open` `NotImplementedException` after a
successful Release build.

## Phase F/G Core implementation (Linux, pending local Windows verification)

- Phase F: trusted local bare Git clone/connect, non-force Push, Fetch,
  FF Pull and two-parent domain-merged Pull; independent Segment contents
  survive and same-Segment changes report explicit conflicts preserving HEAD.
- Phase F conflicts also cover incompatible audio identities and divergent
  music timing-point edits; the legacy floor-aligned transport is unchanged.
- Phase G: immutable committed-chart composition/export with strict completed
  interval coverage, sample-vs-ADOFAI timing checks, authored entry orientation,
  global action floor remapping, permitted action types, source audio SHA256,
  and atomic no-clobber .adofai output verified by the production loader.
- Linux .NET SDK 8.0.423: Core Release build 0 warnings/errors; all 19
  focused Core regressions and the default Core timing regression pass.
  This does **not** constitute Windows WPF or stock-game verification.
- Contract and restrictions: `docs/together-time-first-export-contract.md`.

## Unresolved

- Phase H: Windows Core/WPF build, real two-client GUI workflow and manual
  .adofai/game validation; GUI automation remains prohibited.
- Connect Phase F remote operations and Phase G export validation to WPF UI;
  surface fail-closed diagnostics and explicit playback settings.
- More complex chart events and automated tempo fitting are unsupported by
  Phase G and must not be presented as production-ready.

## Next concrete work

Add Phase F remote operations and a three-way merge for time-first manifests
and independently owned Segment files. Exercise two cloned workspaces, edits to
different Segments, same-Segment conflicts, dirty-worktree protection, and
preservation of local history and parked revisions.
