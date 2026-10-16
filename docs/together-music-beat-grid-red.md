# ExtremeEditor Together — Music Beat Grid Core RED

## Phase A contract

The music grid is defined on the shared decoded-audio sample axis. It is not
derived from ADOFAI floors, `SetSpeed`, `Pause`, or playback pitch.

- A constant timing section has an integer sample rate, positive decimal BPM,
  and an integer beat-origin sample.
- Supported subdivisions are `1/1`, `1/2`, `1/3`, `1/4`, `1/8`, and `1/16`.
- Every grid position is calculated directly from the origin and an integer
  tick index. Repeated addition is forbidden because it accumulates drift.
- Snapping uses the nearest grid line. An exact midpoint is resolved away from
  the beat origin.
- Visible-line enumeration is half-open `[startSample,endSampleExclusive)` and
  bounded by a caller-provided line limit. It must not generate the whole song.
- Results are immutable and preserve the tick index so whole beats can be
  distinguished from subdivisions.

Phase B will compose multiple constant sections into musical timing points.
This phase does not interpret chart timing or synthesize ADOFAI tiles.

## RED

`MusicBeatGridRegression` exercises exact boundaries, all supported
subdivisions, snapping, a large tick index, visible-range bounding, invalid
input rejection, and immutable results.

Expected RED after a successful Release build:

```text
System.NotImplementedException: RED: build a constant BPM grid on the shared audio-sample axis.
```

Run with `EXTREMEEDITOR_MUSIC_GRID_ONLY=1`.
