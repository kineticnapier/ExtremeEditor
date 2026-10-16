# Together time-first authored Segment RED

EditorVersion: `0.0.590-prototype`

Phase E separates sample anchors from real ADOFAI authoring data. A tile is
not publishable merely because it has an audio-sample anchor: it also carries
an explicit destination angle and local actions, while the Segment carries an
explicit entry angle.

The RED requires an owner-scoped `TimeFirstSegmentEditorSession` that:

- opens only an active Segment owned by the requesting editor;
- starts from an explicitly empty persisted draft without inventing content;
- places, edits, and removes real tile geometry only inside `[start,end)`;
- keeps equal-time insertion order and unique tile IDs;
- supports Undo/Redo without mutating prior snapshots;
- validates local action floors and rejects unsafe/mismatched metadata;
- promotes a draft to a named authored revision atomically; and
- saves/reopens that authored revision through the time-first Workspace.

Expected RED: Release build succeeds and
`EXTREMEEDITOR_TIME_AUTHORING_ONLY=1` fails at the unimplemented editor open.
