# Segment timeline allocation — RED (2026-10-15)

## Goal

Git-style distributed collaboration needs **independent editing data and audio-timeline assignment**. A user changing their work allocation from `[50s,100s)` to `[40s,80s)` must not move or discard the already composed `[50s,100s)` notes.

## Initial contract

- Boundaries are half-open `[start,end)` positions on a **common decoded audio sample timeline**. The project declares a sample rate (fixture: 48,000 Hz); no BPM/beats/musical bars for allocation.
- A `SegmentTimePartition` identifies a work slice independently from the saved content revision it references. `ContentRevisionId` and `ContentOriginSample` remain stable when the visible time slice is trimmed.
- `SplitAt` partitions the existing assignment into two views of the same **immutable content revision**, without slicing or rewriting the underlying `LevelSegment` yet.
- `Park` turns the removed tail into a retained, unassigned revision that **does not reserve** audio-timeline space. It may later be restored or independently examined.
- `CreateEmpty` adds a separate new Segment with no existing content. Creation is rejected if it overlaps an active assignment; gaps and adjacent ranges are allowed.
- `Validate` rejects duplicate Segment IDs, invalid/empty ranges, missing sample rate and overlapping **active** assignments.
- Operations must not mutate the input snapshot or the existing LevelDocument/LevelSegment.

Example:

```text
before:     B [50,100)
rearrange:  B-new [40,50) + B-existing [50,80) + B-parked [80,100)
later:      C-new [80,100) while B-parked content remains archived
```

## Scope limits

1. The snapshot only tracks **time allocation metadata**; it does not cut a chart at arbitrary song timestamps or generate a playable standalone slice.
2. Git merge of different files can succeed while time allocations overlap. Semantic validation is still mandatory.
3. Real content slicing, cross-boundary event behavior, mid-rotation cuts, segment-local inherited timing state, Git persistence/push/pull, UI and network synchronization are separate features.
4. Audio decoding and source resampling into the declared project sample timeline are not implemented in this PoC.
5. Parking preserves the original content reference; restoring it while an overlapping active assignment exists needs a future conflict-resolution policy.

## Status

- RED contract: `src/ExtremeEditor.Core/SegmentTimePlanner.cs`
- RED regression: `src/ExtremeEditor.Core.Tests/SegmentTimePlannerRegression.cs`
- `EditorVersion = 0.0.547-prototype` (RED only, Windows verification pending)
- Existing segment split/merge GREEN from `0.0.546` remains separate.

To run only the RED:

```powershell
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
$env:EXTREMEEDITOR_SEGMENT_TIMELINE_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_TIMELINE_ONLY -ErrorAction SilentlyContinue
}
```

Expected: build PASS and `NotImplementedException` from `SegmentTimePlanner.Validate` on test run.
