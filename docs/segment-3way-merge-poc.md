# Segment time 3-way merge — GREEN implementation (2026-10-15)

## Purpose

After floor-aligned Segment split/merge (`0.0.546`) and audio-sample timeline management (`0.0.548`) passed their isolated GREEN tests, the next experiment is **Git-like 3-way merging of timeline assignment snapshots**.

Merge inputs: `commonAncestor`, `ours`, `theirs`, each with an independently valid `SegmentTimeTimeline`. Stable `SegmentId` is the merge identity; collection order is irrelevant.

## Required semantics

- Only one branch changes an existing Segment → adopt its change.
- Both branches change an existing Segment to exactly the same record → converge.
- Both change the same Segment differently → `DivergentEdit` conflict; do not assume independent fields can safely merge.
- One deletes an existing Segment while the other updates it → `DeleteModify` conflict. Delete vs unchanged is safe.
- Both create the same new ID with different data → `ConcurrentCreation` conflict.
- Different new Segment IDs with overlapping **active** time ranges → `ActiveRangeOverlap` conflict, even when Git can merge separate files.
- Parked segments do not reserve their former audio range: preserving/editing the parked content revision and assigning an active Segment in that range is allowed.
- Different audio sample rates → `SampleRateMismatch` conflict.
- On conflict, return no merged timeline (`Timeline == null`) and a structured conflict list. Never silently pick a side or return an invalid allocation.
- Validate all input snapshots via `SegmentTimePlanner.Validate`; reject invalid source input. Validate the successful merge too.
- Do not mutate ancestor/ours/theirs; source content revisions and content audio origins remain unchanged unless explicitly edited.

## Test command

```powershell
git fetch origin
git switch feature/segment-model-poc
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_MERGE_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_MERGE_ONLY -ErrorAction SilentlyContinue
}
```

GREEN expectation: build succeeds and prints `PASS: semantic three-way segment timeline merge regression.`. Windows build and runtime verification are still pending.

## Status and exclusions

- EditorVersion `0.0.549-prototype`: RED added; user confirmed the expected failure.
- EditorVersion `0.0.550-prototype`: semantic merge implemented; **Windows GREEN verification pending**.
- New contract: `src/ExtremeEditor.Core/SegmentTimeThreeWayMerger.cs`.
- New regression: `src/ExtremeEditor.Core.Tests/SegmentTimeThreeWayMergeRegression.cs`.
- **Implemented**: deterministic Segment-ID 3-way comparison, unchanged-side selection, identical-edit convergence, delete-vs-modify/concurrent creation conflicts, post-merge active-range conflict detection and validated conflict-free snapshots.
- **Not implemented**: Git subprocess/repository transport, persistence, merging actual `LevelSegment` chart contents, waveform/sample decoding, arbitrary-time chart cutting, or ADOFAI export validation.
- `ContentRevisionId` is currently a string reference, not verified content-addressed storage. The RED only models metadata and conflicts.
