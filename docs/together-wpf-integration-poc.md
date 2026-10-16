# ExtremeEditor Together — WPF authoring integration GREEN

## Version and scope

- Branch: feature/segment-model-poc, never main
- `0.0.568-prototype`: RED contract created; user authorized continuing through GREEN without a separate RED test report.
- `0.0.569-prototype`: WPF controller and initial nonmodal Together panel implementation committed; **Windows build, GREEN test and interactive UX verification pending**.
- Prior user-confirmed GREEN: 0.0.567-prototype (real two-client Git merge to validated .adofai roundtrip)
- Work happens within the existing WPF editor, not a new parallel chart editor. This is the initial GUI-connected slice; a fully flexible ownership/allocation wizard remains separate work.
- Do not change postponed PlanetRadius/PositionTrack stock tests, run GUI automation/SendKeys, or use CI. The Windows user runs tests. Test Git operations run ONLY against disposable directories inside a freshly generated OS temporary root.

## First WPF Together panel

A new Tools -> Together menu entry opens the TogetherWorkspaceWindow (independent panel/dialog). The panel will display the selected collaboration workspace, Git commit status, Segment floor ranges and **audio sample ranges** (not to be confused), owners, parked/active state, and Pull content/timeline conflicts.

Required named WPF controls for discoverability/structural regression:
- MainWindow: TogetherMenuItem (and OpenTogetherClick)
- TogetherWorkspaceWindow: CreateWorkspaceButton, OpenWorkspaceButton, SegmentList, SaveSegmentButton, CommitButton, FetchButton, PullButton, ExportButton, TogetherStatusText.

Controls must be functional and must handle errors without erasing edits. No simulated clicks, visual comparisons or WPF MainWindow startup in automated tests; reflection checks that the named surface exists. User runs manual UX interaction after GREEN.

## Separation of responsibilities

- Core stays the semantic and Git authority (SegmentGitWorkspace, SegmentGitRemoteSync, SegmentEditedCompositionValidator, SegmentGitAdoFaiPipeline). WPF creates no parallel Git merge/export implementation.
- TogetherWorkspaceController is a WPF-facing adapter with a small presentation state and explicit operations.
- An EditorSession remains the authoring buffer. A Git commit is NOT the same operation as the ordinary .adofai Save. No implicit authoring commit on focus change, selection or shutdown.
- SourceTemplate points to an original real .adofai. The initial Seed Snapshot is supplied by the user/workspace wizard from floor-aligned segments and a **known shared sample rate**; do not invent arbitrary sample cuts.
- The selected Segment must be active and owned by the current owner ID; other owners' regions may be shown read-only.
- StageSelected(EditorSession) compares the editor's full chart with the workspace's composed baseline. ONLY the selected Segment may change, with unchanged floor count, settings and unselected content. Reject unsupported structure edits, foreign floors, invalid ownership, stale workspace and any missing data; never silently shift another owner's assignment.
- Successful staging writes only collaboration-owned Segment files; dirty EditorSession undo history is preserved. Explicit Commit(message) records the staged edits and updates the visible SHA. Fetch never replaces the editor buffer; Pull refuses when the active EditorSession is dirty, and reports conflicts without discarding saved or unsaved data.
- Git Push is explicit, never forced; local bare remote is the only supported transport.
- Export is only allowed for a clean, published, synchronized HEAD and reuses the verified Core exporter. It never exports dirty unpublished content or overwrites the source original.

## Dedicated RED regression

\`src/ExtremeEditor.Wpf.Tests/Editor/TogetherWorkspaceUiRegression.cs\` tests real isolated Git, Core Loader and EditorSession behavior:

1. Create owner A's workspace from a loaded .adofai and floor-aligned A/B snapshot. Status row exposes floor/audio ranges, ownership and writable status; selecting B as A is rejected.
2. Open the cloned workspace as owner B; other owner's floor range is read-only.
3. Use EditorSession.ReplaceAction and the real undo history to edit A's SetHitsound. Stage and explicitly Commit, Push.
4. Reject B trying to stage a Rotate performed on A's floor without touching B's saved Segment or editor history.
5. B edits its own floor by EditorSession.AddAction(SetFloorIcon), commits; diverged Push rejects, Fetch leaves local HEAD/content untouched.
6. Pull with an unsaved EditorSession is rejected. Clean explicit Pull performs semantic merge; B Push, A Fetch/Pull converge to the same commit.
7. UI-facing Export produces a real .adofai that contains A/B's edits plus unknown root/event metadata, preserving the original file.
8. A new unpublished edit cannot overwrite the previously published .adofai.
9. Reflection checks the actual WPF Together menu, handler, window and named controls without GUI automation.

The regression remains isolated and deterministic. The GREEN controller delegates to the previously tested Core operations. GREEN completion awaits Windows build/test confirmation and manual review of the WPF panel.

## Windows GREEN run

NOTE: This is a **WPF-specific** regression, not the Core test runner. The WPF build invokes the existing asset/native build targets, so it may be slower than Core.

\`\`\`powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_TOGETHER_WPF_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj \`
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_TOGETHER_WPF_ONLY -ErrorAction SilentlyContinue
}
\`\`\`

Expected GREEN: **build succeeds** and the dedicated WPF regression prints the PASS line. The original RED was not separately run by the user in this phase.

Future GREEN expected:
\`\`\`text
PASS: WPF Together workspace editor/Git/export boundary regression.
\`\`\`

## Explicitly deferred

- Full general-purpose Segment allocation wizard, arbitrary mid-floor audio splits, changing floor counts with multi-owner reallocation, rich conflict-resolution UI.
- Remote internet/SSH credentials and server-backed realtime collaboration.
- Integrating editor actions into an automatic background commit/synchronization process.
- Stock-engine .adofai gameplay correctness and missing advanced event semantics.
- UI automation is prohibited; human visual/interaction review after GREEN is separate.

## GREEN implementation details and limitations

- Added owner-scoped TogetherWorkspaceController: Create/Open/SelectSegment/StageSelected/Commit, local bare origin Connect/Push/Fetch/Pull, and verified export. Stage checks the EditorSession against the current Git snapshot and refuses edits outside the selected owned floor slice, global settings changes and floor insertion/deletion.
- A restricted Core SegmentGitWorkspace API exposes HeadCommit and the clean-worktree validator; arbitrary Git command execution remains internal.
- Added the WPF TogetherWorkspaceWindow and a MainWindow menu entry. The panel shows segment ownership, floor and absolute audio sample bounds, revision, Git SHA/staging status, and conflict indicators. It calls the existing MainWindow/EditorSession for actual chart editing.
- First-time Create derives whole-floor sample boundaries from the current chart and an explicitly provided decoded audio sample rate; the user supplies cut floors and owner IDs. This does not add arbitrary audio-time cuts or audio decoding.
- The panel is nonmodal. Stage and Commit are separate; remote sync is explicit and local bare only. Reloading the editor is a deliberate user operation and confirms before discarding an unsaved local undo buffer.
- While a Together chart is active, MainWindow blocks ordinary Save/Save As to prevent overwriting the original source .adofai. The committed shared chart is exported separately only from the clean/published revision. Loading an ordinary chart exits the Together view.
- The WPF regression is headless/STA and tests controller flow plus structural existence of named WPF controls; it does NOT automate GUI clicks or establish rendering/interaction correctness. Users should inspect the panel manually after GREEN.
- GUI preview playback setup is limited in this first slice; the full audio/native playback refresh and polished workflow are a follow-on. Existing WPF performance and native Stock Radius/PositionTrack regressions remain untouched.
