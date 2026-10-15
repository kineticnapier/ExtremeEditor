# ExtremeEditor Together — Edited Segment composition GREEN (2026-10-15)

## State and scope

- Branch: `feature/segment-model-poc`
- `0.0.561-prototype`: RED contract, Windows RED confirmed by user.
- `0.0.562-prototype`: GREEN validator implementation committed; **Windows GREEN verification pending**.
- Previous unchanged roundtrip boundary validator passed at `0.0.560-prototype`.
- No changes to `main`, no CI or GUI automation, no edits to the deferred stock PlanetRadius / PositionTrack regression.
- No `.adofai` writer, local Git operation, remote Push/Pull, or export side effect in this phase.

## New contract

`SegmentEditedCompositionValidator.ComposeAndValidate(template, allocations, contentBySegmentId)` operates on the **new edited chart**. Unlike `SegmentBoundaryValidator.ValidateRoundTrip`, it must **not reject legitimate intentional changes** merely because their angles, actions, decorations or floor times differ from the template.

Inputs:
- `template`: global chart settings and floor-independent decorations, not an unedited comparison oracle;
- `SegmentTimeTimeline allocations`: audio-sample-based ownership allocations (independent of BPM), including optional parked revisions;
- `contentBySegmentId`: floor-aligned `LevelSegment` data, explicitly associated with stable allocation IDs.

Return `SegmentEditedCompositionResult`:
- on acceptance, a **new in-memory candidate** `LevelDocument` plus globally recomputed `TimingMap`, zero issues;
- on rejection, `Document = null` and `Timing = null` with typed issues (`MissingActiveContent`, `InvalidFloorCoverage`, `SampleRangeMismatch`, `UnsupportedCrossBoundaryEffect`, `InvalidModeledState`);
- original template, allocations, active edits and parked revisions remain byte-/content-identical on acceptance or rejection.

## Sample alignment policy for this incremental PoC

This is **floor-aligned composition only**. Audio partitions `[StartSample,EndSampleExclusive)` are linked to whole floor intervals via Segment IDs. They **must be consistent with the edited complete chart's global clock**:

- For each active floor Segment `[startFloor,endFloor)`, its `StartSample` is the global audio sample of the composed chart's floor `startFloor` entry.
- Its `EndSampleExclusive` is the next floor entry sample, or, for the final Segment, the composed chart's total modeled duration sample.
- Tolerance is at most one sample for floating-point rounding. The decoded sample rate is taken strictly from `SegmentTimeTimeline.SampleRate`; global pitch/offset/countdown are applied through `PlaybackClock.ChartToAudioTime`.
- Active floor ranges must cover the candidate exactly, with no gaps or overlaps. Audio ranges must correspond to their active floor assignment; an abandoned or stale assignment is a **typed rejection**, not an instruction to shift authored notes.
- Parked revisions retain their original content but never contribute to the assembled active chart; unrelated parked time overlap is allowed by existing `SegmentTimePlanner`.
- Arbitrary `[50,100)`-style mid-floor cuts and remapping parked content to new time windows are **not supported yet**. Never infer an arbitrary sample cut equals an ADOFAI floor cut.

## GREEN acceptance cases

1. **Three independently edited Segments**: A changes incoming angle and earlier SetSpeed; B changes an angle, Pause and nested decoration payload; C changes an incoming angle, SetSpeed and adds Twirl. Recompose with `LevelSegmentPartitioner.Merge`, recompute whole-chart `TimingMap`, preserve edits and accept an allocation built from this **new** global timeline. Both rendered angles and modeled floor times legitimately differ from the template.
2. **Stale allocation**: Keep the original audio ownership intervals after an earlier Segment's BPM/tile edit changes global times. Reject as `SampleRangeMismatch`, without moving data, redefining an author's interval, or silently accepting the drift.
3. **Off-by-24-samples at a cut**: Reject with `SampleRangeMismatch` even if the audio intervals remain nonoverlapping.
4. **Missing active payload / extra active allocation**: Reject as `MissingActiveContent`, never construct partial chart output.
5. **Invalid floor coverage**: Reject a gap or overlap as `InvalidFloorCoverage`.
6. **Parked revision**: Keep it intact, do not merge it into active output, and do not overwrite an active Segment that happens to share floors.
7. **Unresolved cross-Segment effects**: `ScaleRadius`, `PositionTrack`, and a positive-duration `MoveDecorations` beginning before a cut fail closed with `UnsupportedCrossBoundaryEffect`. The old, intentionally deferred stock regression remains untouched.
8. **No mutation**: Snapshot serialized template, active payloads, allocations and parked payloads. All must stay unchanged after both accepted and rejected validation.
9. **No export claim**: `Accepted` means only that the explicitly modeled floor-aligned composition and audio-sample allocations are consistent. It does not prove stock ADOFAI compatibility, visual choreography correctness, exact hit judgment, or lossless/playable `.adofai` output.

## GREEN command (Windows)

```powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_EDITED_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj `
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_EDITED_ONLY -ErrorAction SilentlyContinue
}
```

**Expected GREEN:** build succeeds and the independent edited-composition regression finishes successfully.

The future GREEN success output is:

```text
PASS: edited Segment composition and sample-aligned ownership regression.
```

## Future, explicitly separate steps

- True arbitrary time cuts with physically meaningful author edits at non-floor-aligned samples.
- Stronger semantics and complete cross-boundary effects, including real ScaleRadius and PositionTrack evaluation.
- Project-level audio identity, stored sample origin and user-facing conflict resolution for changing allocations.
- Stock ADOFAI timing/playability oracle, proper export serialization and production transaction safety.

## GREEN implementation notes

- Validate audio allocations with SegmentTimePlanner; link active Segment IDs with owned floor slices; retain Parked content without using it in the composed candidate.
- Check active floor ranges for contiguous, unique, non-overlapping coverage before rebuilding via LevelSegmentPartitioner.Merge, preserving the caller's payloads and source template.
- Recompute a single whole-chart TimingMap and verify all floor states remain finite. Edited SetSpeed, Pause, Twirl and destination-angle changes therefore naturally affect the entire later chart.
- Check each active allocation's start and end decoded audio samples against global floor entry or total duration, including the PlaybackClock Offset/Pitch/Countdown transform. At most one sample of rounding difference is tolerated; mismatched/stale assignments are rejected with no candidate.
- Fail closed on potentially cross-boundary ScaleRadius/PositionTrack or unmodeled positive-duration actions. Reject invalid speed/pause settings and non-finite geometry/timing.
- An accepted result is an in-memory modeled composition, not a lossless .adofai file or a guarantee of ADOFAI stock engine behavior.
- No GUI automation, branch operations, export file creation or time-slice relocation is performed. The deferred ScaleRadius/PositionTrack stock-regression work was not changed.
