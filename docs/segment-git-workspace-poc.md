# Segment Git workspace — GREEN implementation (2026-10-15)

## Goal

Decide whether **real local Git** can provide useful version history and independent editing branches for ExtremeEditor Together, without handing Git responsibility for interpreting ADOFAI timing/event semantics.

Prior prototypes: floor-aligned `LevelSegment` split/merge (0.0.546 PASS), non-destructive audio-sample interval allocation (0.0.548 PASS), and semantic 3-way merging of **allocation metadata** (0.0.550 PASS).

## Isolated Git experiment

- **Scope:** a new, dedicated local Git repository in a user-chosen *empty directory*, never the ExtremeEditor source repository. The regression uses uniquely named OS temporary directories and cleans up only those fixtures.
- Persist one `timeline.json` for the project-time assignment snapshot, with schema version, and a separate `segments/<segmentId>.json` for each floor-aligned `LevelSegment`. Stable IDs, not owners or floor positions, identify files.
- Preserve the `LevelSegment` angle values (including 999 midspin), local floor indices, `LevelAction` metadata and nested source/override JSON, and `LevelDecoration` data without silently dropping unknown modeled properties. This does **not** guarantee lossless arbitrary source `.adofai` export.
- Saving an unchanged snapshot twice must produce **byte-identical files**. Loading must reconstruct independent in-memory objects without unintended mutable JSON aliases.
- `Initialize` refuses nonempty existing directories. `Open` verifies and loads an already initialized collaboration repository.
- `Commit` must stage only dedicated collaboration files, never arbitrary unrelated/untracked files; supply a local-only commit identity rather than changing global Git configuration.
- `CreateBranch` creates independent local branches. `Checkout` refuses pending changes rather than silently stashing, cleaning, resetting, or overwriting them.
- `ReadAt` reconstructs a historical snapshot by revision or branch ref **without changing the current HEAD or worktree**. Three snapshots (base, left, right) must be usable as inputs to the already working `SegmentTimeThreeWayMerger`.
- Storage must reject path traversal/untrusted Segment IDs before writing outside its dedicated workspace or into Git internals.
- **No** network push/pull, real `git merge`, automated creation of a merge commit, Git hooks, GUI, production editor integration or semantic merging of the actual `LevelSegment` content at this stage.

## Isolated GREEN test

`src/ExtremeEditor.Core.Tests/SegmentGitWorkspaceRegression.cs` uses the normal Core.Tests executable, gated by `EXTREMEEDITOR_SEGMENT_GIT_ONLY=1`.

```powershell
git fetch origin
git switch feature/segment-model-poc
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Fresh build failed" }

$env:EXTREMEEDITOR_SEGMENT_GIT_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_GIT_ONLY -ErrorAction SilentlyContinue
}
```

**Expected GREEN:** fresh build succeeds; test prints `PASS: isolated Git segment snapshot/branch regression.` Git CLI on PATH is required. Windows runtime confirmation is pending.

## Status

- `0.0.551-prototype`: initial RED contract/test, subsequently fixed its test syntax in `0.0.552-prototype`; user confirmed expected RED.
- `0.0.553-prototype`: local Git workspace implementation committed; **Windows GREEN verification pending**.
- `0.0.554-prototype`: Windows test reached cleanup and failed with `UnauthorizedAccessException` under a read-only Git object; regression cleanup now clears read-only attributes strictly inside the unique test temp directory, rejects links/junctions, and reports cleanup failures without masking test assertions. **Full GREEN still unverified.**
- Branch: `feature/segment-model-poc`.
- Implemented: marker-protected isolated repository initialization/open, schema-versioned deterministic JSON files, segment-local actions/decorations, local Commit, Branch creation/safe checkout, historical read without checkout, semantic timeline merge input compatibility.
- Git operations use argument arrays (no shell), disable repository hooks, set commit identity per-command, and stage/commit only the schema-owned paths.
- A separate manifest limits removal of obsolete Segment files to previously managed files. Segment identifiers are restricted to safe ASCII filename characters; no user-controlled paths are passed unchecked.
- **Limitations:** Git CLI must be installed; no power-loss atomic snapshot write, file locking, crash recovery, network transport, real Git merge commit, complete .adofai preservation, cross-boundary effect semantics, or threat-model-complete sandboxing.
