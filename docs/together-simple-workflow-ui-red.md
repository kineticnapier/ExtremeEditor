# Together simplified workflow UI RED

EditorVersion: `0.0.607-prototype`

The existing Together window must present a direct administrator workflow of
loading audio, splitting the complete song, assigning participants, and
starting collaboration. Participants must open a collaboration and select
their name before owner-scoped editing is enabled.

The normal section list must use human-readable section numbers and times; it
must not expose internal Segment IDs, floor ranges, or sample coordinates.
Legacy project loading and advanced Git/export controls remain available, but
new collaboration creation must never silently fall back to floor-number
splitting.

Expected RED: the WPF Release build succeeds and
`EXTREMEEDITOR_TOGETHER_SIMPLE_UI_ONLY=1` fails because the simple workflow
controls and handlers have not yet been added.
