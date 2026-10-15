# ExtremeEditor Together — two-client Git-to-.adofai E2E GREEN

## Baseline and limits

- Branch: `feature/segment-model-poc`; do not change `main`.
- `0.0.566-prototype` RED was user-confirmed on Windows.
- `0.0.567-prototype` GREEN is implemented; **Windows GREEN build/run verification is pending**.
- Previous `0.0.565-prototype` validated Segment `.adofai` exporter has **user-confirmed GREEN**.
- Existing components: `SegmentGitWorkspace`, `SegmentGitRemoteSync`, `SegmentGitBranchMerger`, `SegmentEditedCompositionValidator`, `SegmentAdoFaiExporter`, `AdoFaiLoader`. All were tested separately, but real Git history to exported file has not been tested end to end.
- Never modify the user's source Git repository, run GUI automation, run CI or modify the postponed native ScaleRadius/PositionTrack tests.
- All Git activity here is in newly created **isolated OS temporary directories** with two client repositories and a local bare remote. No network or authentication.

## Published-HEAD export bridge

`SegmentGitAdoFaiPipeline.ExportPublishedHead(sourceTemplate, workspace, destinationPath, overwriteExisting = false)` implements the published-commit export gate. The original `.adofai` template must remain external to the collaboration workspace.

Implementation:
1. Require a **clean, committed local main** and validate against the freshly fetched **origin/main** commit. Refuse dirty drafts, unpublished commits, and divergent/conflicted branch tips. Fetch is allowed to update remote-tracking state, but no implicit Pull, Push, branch checkout, stash, reset, or history rewriting.
2. Pin an exact commit SHA; read its segment snapshot with `workspace.ReadAt(sha)`, rather than uncommitted worktree files.
3. Re-run `SegmentEditedCompositionValidator.ComposeAndValidate`, including the active ID-to-floor segment mapping and global decoded-audio sample allocation. Do not silently adjust user-owned sample ranges after SetSpeed or angle changes.
4. Invoke `SegmentAdoFaiExporter.ExportValidated` to construct, write temporarily, reopen with `AdoFaiLoader`, then publish only on a complete validation. Use the **external original raw .adofai** (not a Git workspace-owned file) as the JSON preservation source.
5. Return `SegmentGitAdoFaiPublication(SourceCommit, DestinationPath, FloorCount, ActionCount)` only for a completely verified and published result.
6. Fail without modifying existing source or output file, collaboration drafts/commits, and foreign files on conflicts, invalid allocations or unsafe export.

**Important limitation:** Verification remains model/read-back-level. This does not claim full native ADOFAI playability, exact timing with advanced events, or arbitrary-time Segment slicing. Only two floor-aligned Segments with **non-timing-affecting edits** are accepted in this RED. Rhythm/timing-altering edits that leave sample assignment stale must be rejected rather than automatically moved.

## Isolated E2E test cases

The test uses a real JSON source containing Midspin 999, an event with an unknown property, a CustomBackground event with nested raw payload, a floor-attached decoration, a project-wide decoration, and unknown root/settings JSON.

Positive path:
- Initialize client A and local bare `main`; publish the baseline snapshot.
- Clone client B from the same bare origin.
- A changes a SetHitsound value in floor-region A, commits and pushes; B edits nested CustomBackground and decoration in floor-region B and commits independently.
- B push non-fast-forward **must** reject. B Fetch must preserve local B bytes and HEAD.
- B semantic Pull creates a true two-parent merge commit. B Push publishes it; A Fetch/Pull fast-forwards to that exact shared commit.
- Export the **published pinned Git commit** via the new bridge and load the resulting `.adofai` through `AdoFaiLoader`.
- Verify both authors' edits, unknown root/settings/action/decoration JSON, original Midspin, world floor count, sample-aligned whole-chart TimingMap, and source file untouched.

Negative path:
- Dirty A worktree may not publish and the draft must remain intact.
- A and B independently edit **the same** Segment A; Pull must report typed semantic content conflict without changing B HEAD, index or Segment bytes. Conflicted/unpublished B HEAD must not replace a prior valid export.
- A publishes a new tempo-changing SetSpeed **without updating its audio allocations**: the composition validator must report SampleRangeMismatch, and the end-to-end publisher must refuse replacing the valid output.
- Rejections must preserve existing output bytes and source input bytes.

## Windows GREEN invocation

```powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_TOGETHER_E2E_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj `
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_TOGETHER_E2E_ONLY -ErrorAction SilentlyContinue
}
```

**Expected GREEN:** Build succeeds and the two-client Git-to-export regression finishes with PASS.

**Expected future GREEN:**
```text
PASS: two-client Git semantic sync to published .adofai export E2E regression.
```

## Out of scope

Real remote URL/SSH credentials, concurrent multi-process publication guarantees, crash recovery beyond temporary-file publication, unknown native ADOFAI effect evaluation, WPF integration, arbitrary sample-based floor slicing, conflict UI, and stock gameplay/parity remain separate future work.

## GREEN implementation notes

- Requires `CurrentBranch == main` and a clean collaboration-owned worktree (`workspace.AssertCleanOwnedWorktree`) so an unsaved or staged draft cannot slip into publication.
- Resolves `refs/heads/main^{commit}`, fetches trusted local bare `origin/main` with existing `SegmentGitRemoteSync.Fetch`, rejects any unequal SHA (outdated, unpushed or diverged history), and rechecks the branch/HEAD and clean state before handing off.
- Uses the immutable SHA with `SegmentGitWorkspace.ReadAt(sha)`; no direct export from the current worktree or index.
- Independently validates merged active floor content against global audio sample allocations, then invokes `SegmentAdoFaiExporter.ExportValidated`, which revalidates and performs safe temporary write, loader read-back and final publication.
- Returns the pinned source commit with the destination path, floor count and action count only after successful export. No implicit `Pull`, `Push`, branch move or Git commit is performed.
- No claim of stock ADOFAI gameplay equivalence, arbitrary sample-to-floor slicing, or remote concurrency locking. The test covers two isolated local clients and a bare remote, not external network servers.
