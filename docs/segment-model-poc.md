# Segment model PoC — implementation verification (2026-10-15)

## Scope
This is a **floor-aligned, non-destructive LevelDocument split/merge regression** for a potential Git-backed ExtremeEditor Together. It is not a collaboration backend and does not change production editor behavior.

The initial RED was confirmed on Windows. The first GREEN implementation of `LevelSegmentPartitioner` has been committed, but Windows fresh-build and runtime verification are **pending**.

Run on Windows:

```powershell
git fetch origin
git switch feature/segment-model-poc
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
$env:EXTREMEEDITOR_SEGMENT_POC_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_POC_ONLY -ErrorAction SilentlyContinue
}
```

## Contract
- Each segment owns `[StartFloor, EndFloorExclusive)`. A floor at a cut belongs to the segment on its **right**.
- `DestinationAngles` stores the angle of each destination floor inside the segment. Floor zero has no incoming angle; subsequent segments retain their **first floor's incoming angle**. Concatenation therefore recreates the exact original `angleData`, including `999` midspins.
- Event/decoration floor indices are segment-local during editing, restored to original floor indices during merge. Multiple events on one floor preserve source order and SourceIndex.
- Floor-less decorations remain **project/global content**, never duplicated into each segment.
- The split must not mutate or alias nested event/decorations JSON belonging to the source chart.
- Merge requires exhaustive, contiguous, nonoverlapping floor coverage; gaps, overlap and invalid cuts are rejected.
- No-edit split → merge and split → merge → repartition → merge must retain timing, actions, decorations and basic song settings.
- The reference/template `LevelDocument` retains project settings and global decorations; segments hold local content.

## Explicit non-goals and open questions
1. **Time ranges:** Floor-aligned cuts cannot represent arbitrary song times inside a rotation. The proposed `[50s,100s)` assignment ranges are a separate timeline/ownership layer; converting them to floor boundaries requires an explicit policy.
2. **Independent playback:** A segment requires entry state (BPM/Twirl/MultiPlanet/etc.) and time anchors to preview on its own. Not covered by this RED.
3. **Cross-segment effects:** MoveTrack, MoveDecorations, RepeatEvents, camera/background effects, relative tile references and source index remapping need semantic validation before normal `.adofai` export.
4. **Source fidelity:** `AdoFaiLoader` does not presently materialize all unknown event properties into `LevelDocument`; proving lossless **full .adofai export** requires a source-preserving document representation beyond this modeled-data roundtrip.
5. **Merge semantics:** Git may merge different files containing overlapping time ranges; a project-specific validator must reject temporal overlaps, invalid boundaries and inconsistent chart state.
6. **Durability:** Segment ID/version, conflict detection, persistence, Git storage choice, audio asset references and server security remain undecided.

## Status
- `0.0.545-prototype`: RED-only API contract and Core.Tests regression added; user verified RED.
- `0.0.546-prototype`: floor-aligned splitter/merger implemented. **GREEN is not yet verified on Windows.**
- No Git integration or GUI. Runtime editor still uses the existing unsplit LevelDocument.
- After editing angles, merge currently uses PathBuilder's plain geometry fallback; it does not yet reapply ScaleRadius/PositionTrack from source metadata. This needs an event-aware rebuild before production use.
- Previous independent PositionTrack/ScaleRadius RED remains unresolved on the main lineage.
