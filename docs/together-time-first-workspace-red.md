# Together time-first workspace persistence RED

EditorVersion: `0.0.588-prototype`

Phase D introduces a versioned persistence boundary for music-time-first
Together projects. It is deliberately separate from the legacy floor-aligned
`SegmentGitSnapshot` schema.

The RED requires `TimeFirstSegmentGitWorkspace` to:

- initialize and reopen an isolated local Git workspace;
- store a measured audio identity, musical timing points, sample-time
  allocations, explicit content entries, and one immutable draft per active
  Segment;
- roundtrip a project whose Segments contain zero tile anchors;
- preserve equal-time anchor order and freeze caller-owned collections;
- reject invalid timing/audio/allocation/draft metadata before writing;
- report a missing Segment payload as missing data, never as an empty Segment;
- commit only files owned by the time-first schema; and
- reject opening a legacy floor-aligned workspace as time-first without
  modifying it.

Expected RED: the Release build succeeds, then
`EXTREMEEDITOR_TIME_WORKSPACE_ONLY=1` fails from the unimplemented
`TimeFirstSegmentGitWorkspace.Initialize` entry point.
