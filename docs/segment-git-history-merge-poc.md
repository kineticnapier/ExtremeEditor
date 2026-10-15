# Git history ↔ semantic Segment merge — RED contract (2026-10-15)

## Status

- Branch: `feature/segment-model-poc`
- EditorVersion: `0.0.555-prototype`
- **RED only.** User previously confirmed GREEN for isolated Git save/branch/read at `0.0.554`; the new bridge is intentionally unimplemented. Windows RED verification pending.
- No changes to `main`, no local development repository operations.

## Scope

Connect real **local Git history and its merge-base** with the already tested time-allocation 3-way merger and saved floor-aligned `LevelSegment` payloads. This is a dedicated collaboration Git repository created in a random OS temporary directory, never the ExtremeEditor source checkout.

Expose `SegmentGitBranchMerger.MergeIntoCurrent(workspace, sourceBranch)`, with a result carrying:
- the automatically discovered `BaseCommit`;
- `MergeCommit` only for a conflict-free, fully written **two-parent** merge;
- structured `TimelineConflicts`;
- `ContentConflictSegmentIds` for contradictory changes to the same underlying Segment data.

## GREEN acceptance contract (the regression is currently RED)

1. Two divergent branches share `base`. A modifies Segment A; B modifies Segment B, including independent content revision references and angle data. The merger must determine `git merge-base` itself, integrate the timeline via `SegmentTimeThreeWayMerger`, integrate floor-aligned content per stable Segment ID, then create a **real Git commit with first parent = B's former HEAD and second parent = A's HEAD**. Ensure source ref is unchanged, and read both updates from HEAD, worktree and `ReadAt(mergeCommit)`.
2. Content must be compared independently of `ContentRevisionId`: two branches changing Segment A to *different angle data while keeping the same revision metadata* is a `ContentConflictSegmentIds=["A"]` conflict, not an automatic pick. This RED does not require intra-Segment action-by-action merges.
3. Independently valid new active time partitions `X=[20s,30s)` and `Y=[25s,35s)` must produce a `SegmentTimeMergeConflictKind.ActiveRangeOverlap` conflict, even though Git could combine unrelated files.
4. For any conflict, **do not change HEAD, source refs, worktree bytes, index/status or Git's merge state**; return `MergeCommit == null`. Never auto-resolve a conflict by silently choosing one branch.
5. Reject non-clean collaboration worktree before merge; preserve pending edits without stashing, resetting or cleaning.
6. Ignore unrelated untracked worktree files: never include them in the merge commit, modify or delete them.
7. Test cleanup handles Windows read-only Git objects but refuses to follow reparse-point links. No production user's repository is ever removed.

## Explicit non-goals

- Remote push/pull, authentication and access control.
- Intra-Segment event-by-event/angle-by-angle semantic merging or conflict UI.
- Automatic construction of physically valid joined ADOFAI chart from independent audio intervals, cross-boundary effects, arbitrary-time tile cuts or lossless `.adofai` export.
- Multi-merge-base (crisscross histories), fast-forward optimization, recovery from disk/power failure or process concurrency; define these before production use.
- No native `git merge` conflict markers or changes to the source-code repository.

## RED test command

```powershell
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_GIT_MERGE_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_GIT_MERGE_ONLY -ErrorAction SilentlyContinue
}
```

**Expected RED:** build succeeds, then the test throws `NotImplementedException` from `SegmentGitBranchMerger.MergeIntoCurrent`. Once implemented, GREEN success line should be:

```text
PASS: Git history plus semantic Segment merge regression.
```
