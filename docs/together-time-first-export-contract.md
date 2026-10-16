# Together time-first Phase G — conservative chart publication

`EditorVersion: 0.0.595-prototype`

## Source of truth

The Git workspace persists integer decoded-song audio samples, assigned
half-open time ranges, authored angles and floor-local actions. The music
`MusicTimingPoint` grid is **not** the chart's SetSpeed/Pause playback model.
Exporters receive `TimeFirstAdoFaiSettings` explicitly, including BPM, offset,
pitch and countdown, rather than substituting the music grid BPM silently.

## Complete-chart conditions

- Active Segments must cover `[0, Audio.TotalSamples)` contiguously, without
  overlap, unassigned gaps or missing payloads. Parked allocations are ignored.
- Each active Segment must have real authored destination tiles; an explicitly
  empty Segment is valid for editing but **not** a finished export.
- Whole-Segment metadata, authored tile lists, revision IDs, owned intervals
  and draft anchors must pass `TimeFirstSegmentGitWorkspace.ValidateSnapshot`.
- Each Segment's `EntryAngle` is checked against the composed ADOFAI floor
  entry orientation in degrees; there is no silent seam adjustment.
- Each authored destination floor must reach its declared song sample within
  one sample according to `TimingMapBuilder` and `PlaybackClock`. Anchors are
  never moved silently, nor are extra SetSpeed/Pause events synthesized.
- Actions are mapped from segment-local floor indices onto the composed chart
  floor indices. Unmodeled and potentially cross-boundary events (such as
  PositionTrack, ScaleRadius and MoveDecorations) fail closed pending audits.

## Publication conditions

- Source is an immutable, clean time-first Git HEAD, not unsaved edits.
- A file named `Audio.FileName` must exist **beside** the intended `.adofai`
  output and its SHA-256 must equal the workspace's recorded audio SHA-256.
- Output cannot be written inside the collaboration Git workspace; existing
  outputs require `overwriteExisting: true`. Symlink/junction path components
  are rejected. No original chart is overwritten or assumed to exist.
- The file is first written as a uniquely named temporary file; the normal
  ADOFAI loader reopens it and its actions, geometry and floor timing are
  compared against the candidate. Only then is the final name published.
  Validation failure must preserve any previous output file.

## Intentional limits

- This is **Core-only** and does not assert stock-game playability or manual
  WPF interaction behavior. Phase H must wire and verify the actual UI flow.
- The editor must expose/confirm entry orientation and actual ADOFAI playback
  parameters; a default entry angle or arbitrary snapped sample cannot be
  presumed correct for every chart.
- Unknown event/decorations, duration crossing and advanced geometry/event
  interoperability are explicitly **not** declared supported by this export.
- No automatic tempo fitting, time-warping, trim-to-boundary or preview export.

Windows commands after applying the F/G patch series:

```powershell
 dotnet build src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release
 $env:EXTREMEEDITOR_TIME_GIT_ONLY = '1'
 dotnet run --project src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release --no-build
 Remove-Item Env:EXTREMEEDITOR_TIME_GIT_ONLY
 $env:EXTREMEEDITOR_TIME_EXPORT_ONLY = '1'
 dotnet run --project src/ExtremeEditor.Core.Tests/ExtremeEditor.Core.Tests.csproj -c Release --no-build
 Remove-Item Env:EXTREMEEDITOR_TIME_EXPORT_ONLY
```
