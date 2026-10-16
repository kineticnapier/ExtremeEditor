# ExtremeEditor Together — Time-first empty Segment Core RED (2026-10-16)

## Goal and scope

Design source: [together-time-first-design.md](together-time-first-design.md).

**Users can create ownership slices from musical time even if no note/tile exists.** A successful creation must produce an *explicit empty content entry*. An absent dictionary key, unreadable content revision, or a null entry is **not** the same thing as a legitimate empty segment.

This is the **first RED** of the agreed time-first refactor, not the full time-first chart composer.

- Branch: `feature/segment-model-poc`
- Previous UI version: `0.0.575-prototype`
- RED version: `0.0.576-prototype`
- GREEN to be requested **only after user confirms RED**.
- No change to `main`, no CI, GUI automation, stage/push, export or source repo Git operations in tests.
- Do not alter deferred PlanetRadius/PositionTrack regression.

## Narrow new API contract

`src/ExtremeEditor.Core/SegmentContentPlanner.cs`:

- `SegmentContentKind.Empty`: real, explicitly represented content state for a time allocation with `ContentRevisionId == null` and **no** `LevelSegment` payload. Valid even if all active segments are empty.
- `SegmentContentKind.FloorAligned`: compatibility slot for already-authored floor-backed PoC content, with matching revision identity and present payload. **Not a declaration that a new empty segment is floor-aligned.**
- `SegmentContentPlanner.CreateEmpty(SegmentTimePartition)`: creates the legitimate content slot for a new active, assigned, sample-time segment. Does not generate floors, dummy angles, event data, or arbitrary sample-to-floor mappings.
- `SegmentContentPlanner.Validate(SegmentTimeTimeline, IReadOnlyDictionary<string, SegmentContentEntry>)`: validates explicit content slots without merging a chart. Returns typed issues for:
  - `MissingActiveEntry`: active segment has **no** content entry.
  - `InvalidContentEntry`: present but null/incorrectly shaped entry (e.g. empty with a fake floor).
  - `RevisionMismatch`: content revision differs from the assignment or an authored allocation is claimed to be empty.
  - `UnknownAllocation`: entry does not refer to any active/parked segment.
  - `InvalidTimeline`: broken sample/owner allocation.
- Parked authored data remains retained and may overlap active time assignments because it does not reserve active space.
- Neither source `LevelDocument` nor `LevelSegment`, timeline, or caller-owned dictionary may be mutated.

### RED examples

```text
Audio timeline:      [0,40)         [40,80)        [80,120)
Owner:                 A               B               C
Content status:        Authored        Empty           Authored
Content entry:         A:r1            B:Empty         C:r1      => VALID

Missing key B (no content slot)                                  => REJECT
B:Empty with a floor-aligned tile                                 => REJECT
A:Empty while time assignment refers to A:r1                      => REJECT
C:FloorAligned with missing payload                               => REJECT
Revision identity mismatch                                       => REJECT

Verse [10,20) and chorus [20,40) with zero authored tiles         => VALID
Parked B-old revision overlapping active B-empty                 => VALID
```

## Deliberate limits

- Current `SegmentTimePlanner.CreateEmpty` creates allocation **metadata** only. This RED adds the corresponding content-state contract.
- Current `SegmentGitSnapshot`, `SegmentGitWorkspace.Save`, `SegmentEditedCompositionValidator`, `LevelSegmentPartitioner.Merge` and `.adofai` Export still require existing floor-backed content. They are unchanged and **do not yet accept time-first empty composition**.
- The next planned slice will connect content-state slots to a real segment editing context and eventually storage/semantic merging; do not silently weaken existing export validators to pass an unfinished chart.
- BPM grid, audio decoding, timing-point editing, two-stage time search, and per-segment playback entry state remain separate RED/GREEN phases.

## Windows RED command

Run on the feature branch:

```powershell
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed: RED not established" }

$env:EXTREMEEDITOR_SEGMENT_EMPTY_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_EMPTY_ONLY -ErrorAction SilentlyContinue
}
```

**Expected RED:** build passes, test enters `SegmentEmptyContentRegression.Run`, then fails at `SegmentContentPlanner.CreateEmpty` with:

```text
System.NotImplementedException: RED: create an explicit empty content entry for a valid audio-time allocation.
```

A compile failure or failure in an unrelated regression **is not** successful RED confirmation.

Once the user reports `成立`, implement the narrow GREEN under the next editor version, run the dedicated test, and preserve the previously passed floor-aligned regression behavior.

## GREEN implementation: `0.0.577-prototype` (Windows verification pending)

User confirmed the dedicated RED with `成立`. The implementation now:

- `SegmentContentPlanner.CreateEmpty` accepts only **valid, owned, active audio-sample assignments with no content revision** and returns a real `SegmentContentEntry(Empty)`. It creates no `LevelSegment`, angle, floor, action, or decoration.
- Rejects attempts to call `CreateEmpty` on a parked, invalid-range, unowned, or previously-authored assignment.
- `SegmentContentPlanner.Validate` first validates timeline ownership, returning `InvalidTimeline` rather than assembling any content.
- An **explicit valid Empty** slot passes; a **missing active dictionary key** returns `MissingActiveEntry`; a null or malformed entry returns `InvalidContentEntry`; a fake empty slot for a referenced revision returns `RevisionMismatch`; an orphan key returns `UnknownAllocation`.
- Existing floor-aligned content is checked for a matching nonempty revision reference and a structurally valid payload, **without linking the data to arbitrary sample cuts**.
- Parked content entries remain optional; provided parked data is checked, but parked revisions do not occupy current authoring-time allocations.
- This step neither changes the existing floor-aligned `SegmentGitSnapshot` schema nor relaxes its legacy composition/export requirements.
- Additional test cases: mismatch between entry ID and dictionary key, bad floor-angle count, invalid sample rate, invalid `CreateEmpty` inputs.
- Changes to input timeline, `LevelDocument`, existing `LevelSegment` payloads, or entry dictionary are prohibited and regression-tested.

### Windows GREEN command

```powershell
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_EMPTY_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "GREEN regression failed" }
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_EMPTY_ONLY -ErrorAction SilentlyContinue
}
```

Expected once verified by user:

```text
PASS: empty time-first Segment content and missing-entry regression.
```

**Do not mark GREEN confirmed until the user reports the actual Windows build and test result.**
