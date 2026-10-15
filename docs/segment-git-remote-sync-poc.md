# ExtremeEditor Together — two-client bare Git remote sync GREEN (2026-10-15)

## Baseline

Confirmed GREEN: floor-aligned Segment model (0.0.546), sample-time partitions (0.0.548), timeline 3-way merge (0.0.550), isolated Git workspace/history (0.0.554), and local Git merge-base + two-parent semantic merge (0.0.556).

The user confirmed the expected RED failure at `0.0.557-prototype`. `0.0.558-prototype` implements the transport contract. **Windows GREEN verification is pending.**

## Experiment boundary

All repositories are created under uniquely named OS **temporary directories**:
- one local **bare** Git repository as the explicit `origin`;
- an independently edited `client-A` collaboration Git workspace;
- a cloned, independently edited `client-B` collaboration Git workspace.

No internet, credentials, GitHub, remote Git protocols, global Git settings, editor GUI, or source repository mutations are needed.

`SegmentGitRemoteSync` supplies:
- `ConnectLocalBare(workspace, barePath)`: explicitly select a **local bare repository**; reject a normal worktree or invalid path.
- `CloneLocalBare(barePath, destination)`: clone and verify the ExtremeEditor Together marker/schema. Refuse a nonempty existing destination, preserving unrelated files.
- `Push(workspace, branchName = "main")`: publish **only the requested branch**, without `--force`. A rejected non-fast-forward push must preserve the bare remote's current history.
- `Fetch(workspace, branchName = "main")`: update remote-tracking history and return the fetched full SHA without touching local HEAD, index, owned files, or uncommitted drafts; can run on a dirty working tree.
- `Pull(workspace, branchName = "main")`: fetch, then combine the remote branch with the checked-out local branch **semantically** (fast-forward where possible; otherwise a two-parent merge). Do not run `git pull` with a text auto-merge of `timeline.json`.

Pull returns `SegmentGitPullResult` with `RemoteCommit`, nullable `LocalCommit`, `TimelineConflicts`, and `ContentConflictSegmentIds`. A semantic conflict returns no local commit, preserves HEAD/index/working bytes, and does not push or overwrite the remote.

## GREEN acceptance scenarios

1. **Publication/clone**: A publishes a base snapshot to bare `origin`; B clones to a clean destination with exactly the same Segment angles and revisions. Non-bare remotes and nonempty clone destinations must be rejected without data loss.
2. **Divergence/Push protection**: A commits a change to Segment A and pushes. B independently commits Segment B; its direct non-fast-forward Push is rejected. The remote remains at A's commit, B's local HEAD at B's commit.
3. **Fetch**: B fetches A, updating only `refs/remotes/origin/main`. HEAD and working-tree bytes remain unchanged; an untracked personal note survives.
4. **Pull and publish**: B pulls and automatically merges its Segment B edits with A's Segment A edits. A real two-parent merge commit results, with B's prior SHA first and A's SHA second; both contents survive. Pull does not implicitly push or commit unrelated files. B explicitly pushes the merged result.
5. **Fast-forward catch-up**: A fetches and pulls B's published merge commit. It reaches the **same** SHA by fast-forward, without creating an unnecessary new merge commit.
6. **Actual content conflict**: A and B independently modify the same Segment's angle data **without changing its ContentRevisionId**. B's Pull returns a content conflict, with unchanged local HEAD, index, status, timeline bytes, Segment bytes, and unchanged bare remote.
7. **Dirty worktree**: Pull refuses a pending local draft and leaves it byte-identical. Fetch remains allowed and does not overwrite the draft.
8. **Sample-time conflict**: Independently new `X=[20s,30s)` and `Y=[25s,35s)` active allocations conflict after Fetch/Pull; preserve both clients' committed histories and all working content.
9. Cleanup only the exact unique test fixtures; handle Windows read-only Git objects and never traverse links/junctions.

## Running GREEN on Windows

```powershell
git pull --ff-only

dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_GIT_REMOTE_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj `
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_GIT_REMOTE_ONLY -ErrorAction SilentlyContinue
}
```

**Expected GREEN:** build succeeds, the standalone integration regression finishes without exceptions.

**Future GREEN** must print:

```text
PASS: local bare Git remote Push/Fetch/semantic Pull regression.
```

## Explicitly postponed

- HTTP/SSH network remotes, authentication, credentials, remote authorization and conflict UI.
- Production concurrency locking and crash-safe multi-file transactions.
- Arbitrary time cuts, event-level merging within a single Segment, cross-Segment musical/event state, and lossless/playable `.adofai` export.
- Multi-base/crisscross merge histories, protected branch policies and remote race recovery are not proven by this PoC.


## Implemented strategy at 0.0.558

- Only a **trusted local bare Git repository** can be selected as origin. The location must exist, be bare according to Git, and not overlap with the collaboration worktree. Reject nonempty clone destinations and nested Git checkouts.
- Clone creates an independent local worktree with `--no-hardlinks`; `SegmentGitWorkspace.Open` verifies the marker and schema. A failed clone/schema check is **not** treated as valid collaboration data.
- Push uses an explicit non-force refspec for exactly one branch. A rejected non-fast-forward push must not rewrite published history.
- Fetch updates one remote-tracking ref and only Git metadata, not HEAD/index/worktree or Segment payloads. It remains permitted while a client has an uncommitted draft.
- Pull checks the local collaboration worktree is clean, then fetches. It returns unchanged if already current, fast-forwards a descendant remote commit with `git merge --ff-only`, or delegates a divergent history to `SegmentGitBranchMerger` using a unique temporary local alias of the fetched commit. Only the divergent path creates a semantic two-parent merge commit.
- The temporary alias is deleted after the merge attempt. On semantic conflicts the remote stays unchanged and the local content/index/HEAD remain intact; Pull does not implicitly Push.
- Network protocols, credentials, arbitrary remote URLs, cross-machine permissions, malicious bare repositories, full crash recovery, advanced merge-base histories and playable .adofai export remain unimplemented.
