# ExtremeEditor Together — validated .adofai export RED

## Baseline and gates

- Repository: kineticnapier/ExtremeEditor
- Branch: feature/segment-model-poc; main is not changed
- EditorVersion: 0.0.564-prototype (RED)
- Last confirmed GREEN: edited Segment composition at 0.0.563-prototype
- This phase is **RED only**, intentionally using NotImplementedException in SegmentAdoFaiExporter.ExportValidated. Do not implement GREEN until the user confirms RED.
- No CI, editor GUI automation, WPF save changes, or modifications to the deferred native ScaleRadius/PositionTrack regression.

The existing WPF AdoFaiEditorSaveService reconstructs a source JSON root from an EditorSession, but the Segment PoC lives in Core without an EditorSession. This new exporter must use a **real source .adofai file** as its raw template; creating a JSON document solely from LevelDocument would discard metadata and unrecognized events.

## Export contract

\`SegmentAdoFaiExporter.ExportValidated(sourceTemplate, allocations, editedSegments, destinationPath, overwriteExisting = false)\` must:

1. Re-run \`SegmentEditedCompositionValidator.ComposeAndValidate\` from the provided inputs; do not trust a prior Accepted result or make partial output if sample allocations are stale, active content is missing, floor coverage is invalid, or a cross-Segment effect is unsupported.
2. Require \`sourceTemplate.SourcePath\` to point to a readable real on-disk .adofai file, distinct from the destination. Explicit \`overwriteExisting: true\` **never** permits overwriting the source template file itself.
3. Start from the **raw source JSON root** to preserve unrecognized top-level and settings properties. Replace authored angleData, selected known settings, actions and decorations from the validated assembled chart, while retaining unchanged source metadata and project-wide decorations. Since output is angleData, do not accidentally leave conflicting pathData.
4. Preserve the original source action payload by **SourceIndex** when it is not fully represented in the typed LevelAction model. Apply typed edits (e.g. BPM, Pause duration, floor, active) and overrides without losing unknown properties. Use cloned SourceProperties/PropertyOverrides for edited raw events. All floor numbers after reassembly must be global floor numbers. An unknown newly-authored event with no raw source/overrides must fail rather than silently degrade to just floor/eventType.
5. Re-serialize all decorations (including global ones) with original unknown fields and explicit global floor for floor-attached decorations; retain their order.
6. Write into a **unique temporary file in the destination directory**; reload that file with \`AdoFaiLoader.Load\` and compare modeled angles, actions, global audio settings, floor counts, event counts, decorations, full-chart timing and essential raw JSON payloads. Do not publish if validation or read-back fails.
7. Publish only on successful validation/reload: preserve the previous destination unless \`overwriteExisting: true\` was explicitly requested. On rejection or serialization/IO failure preserve the previous destination's bytes, the original source's bytes, and all user-authored Segment data; clean up temporary files.
8. Return \`SegmentAdoFaiExportResult(DestinationPath, FloorCount, ActionCount)\` only after publication. Do not return a success result for partially written output.
9. This is **model roundtrip correctness**, not certification of stock ADOFAI judgement, effect animation, audio asset availability, or playable lossless fidelity.

## Isolated RED regression

The regression creates a unique temporary directory with a real .adofai JSON source containing:
- edited destination angles and a 999 midspin;
- SetSpeed, Twirl, Pause, unknown source properties on known events;
- an edited CustomBackground raw nested payload;
- a future unknown event retained through SourceIndex;
- floor-attached and floorless decorations with nested fields;
- unknown settings/root metadata;
- a new Twirl event with no source index.

It composes edited A/B/C segments, derives valid decoded sample allocations from the **new** whole-chart TimingMap, calls the exporter, reloads with the production loader, and checks both typed/timing parity and raw unknown JSON retention.

Rejection tests cover:
- existing output with overwrite disabled;
- stale audio allocations, even when overwrite=true;
- attempts to overwrite sourceTemplate.SourcePath;
- a newly authored unknown event missing a lossless raw representation;
- source, destination, Segment data and allocation byte preservation;
- no leftover temporary export files.

Only temporary files are created. Tests must not modify the source repository, main branch or WPF editor save routines.

### Windows RED command

\`\`\`powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$env:EXTREMEEDITOR_SEGMENT_EXPORT_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj \`
        -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_EXPORT_ONLY -ErrorAction SilentlyContinue
}
\`\`\`

Expected **RED**: build succeeds, then NotImplementedException from SegmentAdoFaiExporter.ExportValidated.

Future expected **GREEN**:

\`\`\`text
PASS: validated edited Segment .adofai export and roundtrip regression.
\`\`\`

## Explicitly postponed

- Real game load/playback correctness, native renderer parity and incomplete/unsupported Cross-Segment effect models.
- A general editor-wide replacement for the WPF save service.
- Mid-floor arbitrary audio sample slicing; assigning notes to audio ranges without floor alignment.
- Reliance on user-supplied audio paths, external assets, network files, Git publishing, credential or permissions management.
- Crash-safe transaction guarantees beyond single-destination temporary-file publication.
