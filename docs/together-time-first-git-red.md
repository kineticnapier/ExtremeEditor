# Together time-first Git collaboration RED

EditorVersion: `0.0.592-prototype`

Phase F extends the local bare-Git workflow to versioned time-first Segment
payloads. Git history is transport and ancestry; domain snapshots decide what
can be merged.

The RED covers two independent workspaces and requires:

- trusted local bare-repository connect, clone, fetch, non-force push;
- fast-forward Pull and a two-parent semantic merge for divergent history;
- independent edits to different Segment files to survive the merge;
- concurrent edits to the same Segment to return an explicit conflict without
  moving HEAD or replacing either local payload;
- dirty owned files and uncommitted authored data to block Pull;
- local commit history to remain intact; and
- legacy floor-aligned remote behavior to remain unchanged.

Expected RED: Release build succeeds and
`EXTREMEEDITOR_TIME_GIT_ONLY=1` fails at the unimplemented local-bare connect.
