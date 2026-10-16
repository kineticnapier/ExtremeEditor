# Together simple section planning RED

EditorVersion: `0.0.605-prototype`

The administrator-facing planning model must hide Segment IDs and audio sample
coordinates while retaining exact integer-sample boundaries internally.

The RED requires `TogetherSectionPlanner` to:

- initialize one unassigned section covering `[0, TotalSamples)`;
- split at an interior sample using an automatically generated internal ID;
- join adjacent sections and move their shared boundary;
- assign or change a participant name;
- preserve exact whole-song coverage with no gaps, overlaps, or zero lengths;
- reject finalization while any section is unassigned; and
- finalize to the existing immutable `SegmentTimeTimeline` without mutating
  caller-owned plans.

Expected RED: the Core Release build succeeds, then
`EXTREMEEDITOR_TOGETHER_SECTION_PLAN_ONLY=1` fails at the unimplemented
whole-song initializer.
