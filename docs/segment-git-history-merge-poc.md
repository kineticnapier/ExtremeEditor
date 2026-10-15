# Git history ↔ semantic Segment merge — GREEN implementation (2026-10-15)

## Status

- Branch: `feature/segment-model-poc`
- `0.0.555-prototype`: RED contract; user confirmed expected runtime failure.
- `0.0.556-prototype`: implemented Git history bridge; **Windows GREEN verification pending**. Prior isolated Git save/branch/read passed at `0.0.554`.
- No changes to `main`, no local development repository operations.

## Scope

Connect real **local Git history and its merge-base** with the already tested time-allocation 3-way merger and saved floor-aligned `LevelSegment` payloads. This is a dedicated collaboration Git repository created in a random OS temporary directory, never the ExtremeEditor source checkout.

Expose `SegmentGitBranchMerger.MergeIntoCurrent(workspace, sourceBranch)`, with a result carrying:
- the automatically discovered `BaseCommit`;
- `MergeCommit` only for a conflict-free, fully written **two-parent** merge;
- structured `TimelineConflicts`;
- `ContentConflictSegmentIds` for contradictory changes to the same underlying Segment data.

## GREEN acceptance contract (implemented; Windows runtime verification pending)

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

## GREEN test command

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

**Expected GREEN:** build succeeds and the regression prints:

```text
PASS: Git history plus semantic Segment merge regression.
```

## Implementation notes / remaining work

- An isolated alternate Git index starts from the **current HEAD**, stages only collaboration-owned paths, and prepares a two-parent commit via `git write-tree` + `git commit-tree`. Only after snapshot and tree validation does an atomic `git update-ref ... <expected-old-SHA>` advance the current collaboration Branch.
- The normal Git index stages only collaboration-owned paths; unrelated untracked/staged files are deliberately not part of the generated merge tree.
- No native `git merge` is invoked: conflicts are determined from actual saved `LevelSegment` content plus the existing `SegmentTimeThreeWayMerger`. Different content changes to the same Segment are reported; no line-by-line/event-by-event reconciliation is attempted.
- On a Git or filesystem exception before the ref update, the implementation attempts to restore the committed pre-merge collaboration snapshot without invoking `reset`, `stash`, or `clean`. Crash-consistency/concurrent processes and advanced histories are not production-ready.
- The input `ContentRevisionId` is a user-supplied reference and does **not** establish content equality. Whole-segment serialized content is compared separately.
- Not supported: remote Push/Pull, fast-forward/no-op history optimizations, multiple merge bases, production `.adofai` build, mid-rotation stitching, cross-Segment effects, GUI.
