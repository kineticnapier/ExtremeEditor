# ExtremeEditor Together — Segment boundary integrity GREEN (2026-10-15)

## Baseline

- Existing Segment floor split/merge, sample-time partition metadata, three-way merge, local Git and two-client bare Git transport PoCs have all passed their corresponding Windows tests through `0.0.558-prototype`.
- `0.0.559-prototype`: RED contract; user confirmed expected `NotImplementedException` failure.
- `0.0.560-prototype`: validator implemented; **Windows GREEN verification pending**.
- This work remains on `feature/segment-model-poc`. Do not merge to `main` or run any source-repository Git operation in tests.

## Narrow verification scope

We need a **pre-export guard**, but there is not yet a reliable `.adofai` writer or native ADOFAI playback-state oracle in this PoC. Therefore this RED deliberately tests **unchanged floor-aligned Segment roundtrips against a supplied reference LevelDocument**, not arbitrary modified chart approval.

`SegmentBoundaryValidator.ValidateRoundTrip(reference, segments, sampleAnchors?)` must:

1. Validate floor coverage using the existing `LevelSegmentPartitioner.Merge`. Reject malformed, gapped, overlapping or out-of-bounds Segment arrays rather than treating them as valid.
2. Compare the original reference level against its roundtrip for every floor, with extra checks at Segment boundaries: effective BPM and Twirl direction, pause/duration, entry/exit/angle state and derived `TimingMap` floor times. A SetSpeed, Twirl or Pause just before a cut must still influence subsequent floors.
3. Compare resolved `LevelDocument.Positions` (world geometry) and `TimingMap.GetPose` (stationary/orbiting planet position and progress at representative times) in the **same chart-time basis**. Midspins (999) and incoming destination angles at floor cuts must remain intact.
4. Compare event payloads, identity/floor/active state, source order and nested JSON. Unknown event content must not vanish simply because the current TimingMap does not interpret it.
5. Check **explicitly provided floor-entry audio sample anchors**, accounting for global `offset`, `pitch`, and countdown transform via `PlaybackClock.ChartToAudioTime`. A tolerance of at most one decoded sample is permitted. **This does not claim arbitrary time partitions coincide with floors**; a cut inside a floor cannot be certified by this limited API.
6. Fail closed when a cut follows potentially persistent/animated **`ScaleRadius` or `PositionTrack`** effects that the current loader/timing/pose model does not fully resolve; similarly refuse unverified cross-cut duration effects such as `MoveDecorations`. Even a byte-identical roundtrip cannot be called visually correct without resolving those semantics.
7. Produce structured issues: `EventDrift`, `TimingDrift`, `GeometryDrift`, `PlaybackPoseDrift`, `SampleAnchorMismatch`, `UnsupportedCrossBoundaryEffect`. `CanExportModeledRoundTrip` is true only with zero issues. This is **not** approval for stock-game export.
8. Do not mutate the reference level or any Segment input. This validation must never perform actual file export, Git operations, or GUI automation.

The test includes a baseline with `SetSpeed`, `Twirl`, `Pause`, `999` Midspin and two floor cuts; then removes events, changes boundary angles/raw JSON/sample anchors and introduces unresolved geometric/timed events. All should be rejected with the matching typed reason.

## Isolated Windows GREEN

```powershell
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_BOUNDARY_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj `
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_BOUNDARY_ONLY -ErrorAction SilentlyContinue
}
```

**Expected GREEN:** build succeeds, the standalone boundary integrity test completes without exceptions.

After implementing GREEN and confirming it on Windows, expected terminal output:

```text
PASS: floor-aligned Segment boundary timing/geometry/sample regression.
```

## Still outside this contract

- Comparisons against authoritative stock ADOFAI movement and timing, including the separate deferred PlanetRadius/PositionTrack stock-behavior regression.
- Event-specific evaluation of the true `ScaleRadius` and `PositionTrack` animation/state machine across a boundary.
- Arbitrary audio-sample cuts through a floor; a `SegmentTimeTimeline` cannot yet be converted directly into floor slices.
- Accepting legitimate independently edited charts without a new trusted baseline, conflict-resolution policy and full geometry/event resolver.
- Full playable/lossless `.adofai` export, source-file roundtrip, and Git publication gating.

## Implementation at 0.0.560

- Reconstruct the complete floor-aligned chart with LevelSegmentPartitioner.Merge(reference, segments), which rejects missing or overlapping floor coverage before comparison.
- Compare typed and nested JSON action/decoration payloads, source ordering, raw incoming angles (including the 999 midspin sentinel), and world positions without modifying inputs.
- Build whole-chart TimingMaps on both sides so earlier SetSpeed, Twirl, Pause and MultiPlanet state flows across Segment boundaries. Compare per-floor entry/exit times, angular state, effective BPM, direction and pause.
- Evaluate TimingMap.GetPose at fixed fractions of each reference floor interval, comparing stationary/orbiting positions, active planet role and progress at matching chart times.
- For optional floor-entry audio sample anchors, use PlaybackClock.ChartToAudioTime with global Offset/Pitch/Countdown. Require one distinct anchor for each cut when supplied and reject drift exceeding a one-sample rounding allowance.
- Fail closed on active potentially persistent ScaleRadius/PositionTrack before a boundary, and on unresolved positive-duration actions that may continue across it (e.g. MoveDecorations).
- A clean result certifies only the currently modeled unchanged roundtrip. It does not prove stock-game parity or lossless/playable export; deferred stock PlanetRadius/PositionTrack work remains separate.
- No .adofai writer, editor GUI, Git or network behavior was added by this validator.
