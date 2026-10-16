# ExtremeEditor Together — Musical Timing Points Core RED

## Phase B contract

A musical timing map is an immutable, ordered set of BPM timing points on the
shared decoded-audio sample axis.

- The first point is the extrapolation origin before its own sample and until
  the next point.
- Every later point begins a new half-open timing section and is a whole-beat
  origin for its BPM. The preceding section ends exclusively at that sample.
- Input order is semantic and must already be strictly increasing. Build never
  sorts malformed input into apparent validity.
- Visible grid generation splits at timing-point boundaries, emits a boundary
  once from the new section, and applies one total line budget.
- Snapping considers valid lines on both sides of a timing-point boundary.
- Caller-owned point collections are copied and cannot mutate a built map.

This remains a music-authoring grid. ADOFAI `SetSpeed`, `Pause`, `Twirl`, and
floor arrival times are separate chart semantics.

## RED

Run `MusicTimingPointRegression` with
`EXTREMEEDITOR_MUSIC_TIMING_ONLY=1` after a Release build.

Expected failure:

```text
System.NotImplementedException: RED: build immutable musical timing sections on the audio-sample axis.
```
