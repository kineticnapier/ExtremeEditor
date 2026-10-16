# ExtremeEditor Together — WPF Music Timeline RED

## Phase C contract

- Decode the chart's real audio file through the existing NAudio-based audio
  layer and expose its measured sample rate and duration. A default value must
  never be presented as a measurement.
- Retain a bounded signed min/max waveform summary so long songs do not create
  one WPF object per decoded sample.
- Project only the visible sample window into waveform columns, musical grid
  lines, and sample-time Segment bands.
- Support zoom around the pointer, horizontal scrolling, and all six beat
  subdivisions from Phase A/B.
- Mouse selection snaps both endpoints and produces a nonempty half-open sample
  interval. It does not depend on an existing ADOFAI floor.
- The WPF surface is Japanese-first and exposes BPM, beat offset, subdivision,
  measured audio information, selection bounds, owner, and an explicit
  time-Segment creation action.

Phase D will persist the selected time ranges in a versioned Together
workspace. This phase does not weaken the existing floor-aligned Git/export
validators.

## RED

Run `TogetherMusicTimelineRegression` with
`EXTREMEEDITOR_TOGETHER_TIMELINE_ONLY=1` after a Release WPF test build.

Expected first failure:

```text
System.NotImplementedException: RED: decode a bounded waveform summary from the real audio source.
```
