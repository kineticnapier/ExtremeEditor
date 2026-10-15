using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Standalone fail-closed boundary validation contract. All samples are
/// derived from the same global chart/audio clock, never from segment-local
/// BPM, and no test writes or exports an .adofai file.
/// </summary>
internal static class SegmentBoundaryValidationRegression
{
    private const int SampleRate = 48_000;

    public static void Run()
    {
        LevelDocument baseline = Chart();
        LevelSegment[] parts = LevelSegmentPartitioner.Split(baseline, 3, 6).ToArray();
        Check(parts.Length == 3 && parts[1].StartFloor == 3 &&
              parts[2].StartFloor == 6, "test has two real floor boundaries");
        Check(parts[0].DestinationAngles.Contains(999d),
            "the midspin is preserved on the left side of the first cut");

        // Anchors refer only to floor ENTRY samples. These are not the
        // arbitrary time assignments used by SegmentTimeTimeline.
        var anchors = new[]
        {
            AtEntrySample(baseline, 3),
            AtEntrySample(baseline, 6)
        };
        SegmentBoundaryValidationResult exact =
            SegmentBoundaryValidator.ValidateRoundTrip(baseline, parts, anchors);
        Check(exact.CanExportModeledRoundTrip && exact.Issues.Count == 0,
            "unchanged roundtrip with SetSpeed/Twirl/Pause state across cuts must pass");

        // The first Segment changes BPM and toggles direction; removing those
        // events shifts state at floor 3 even though all angles are unchanged.
        LevelSegment[] noSpeed = ReplaceParts(parts, 0,
            parts[0] with
            {
                Actions = parts[0].Actions
                    .Where(a => a.EventType != "SetSpeed").ToArray()
            });
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, noSpeed, anchors),
            SegmentBoundaryIssueKind.TimingDrift,
            "missing earlier SetSpeed affects the next Segment");

        LevelSegment[] noTwirl = ReplaceParts(parts, 0,
            parts[0] with
            {
                Actions = parts[0].Actions
                    .Where(a => a.EventType != "Twirl").ToArray()
            });
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, noTwirl, anchors),
            SegmentBoundaryIssueKind.TimingDrift,
            "missing earlier Twirl affects crossing orientation and rotation time");

        LevelSegment[] noPause = ReplaceParts(parts, 0,
            parts[0] with
            {
                Actions = parts[0].Actions
                    .Where(a => a.EventType != "Pause").ToArray()
            });
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, noPause, anchors),
            SegmentBoundaryIssueKind.TimingDrift,
            "missing Pause alters entry time of the subsequent Segment");

        // An edit to an incoming angle cannot rely on Merge's geometry
        // fallback as proof that resolved playback geometry is still correct.
        LevelSegment[] changedAngles = parts.ToArray();
        double[] alteredAngles = parts[1].DestinationAngles.ToArray();
        alteredAngles[0] = 210;
        changedAngles[1] = parts[1] with { DestinationAngles = alteredAngles };
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, changedAngles),
            SegmentBoundaryIssueKind.GeometryDrift,
            "changed incoming tile direction is not the original geometry");

        LevelSegment[] lostMidSpin = parts.ToArray();
        double[] noMidSpin = parts[0].DestinationAngles.ToArray();
        noMidSpin[^1] = 0;
        lostMidSpin[0] = parts[0] with { DestinationAngles = noMidSpin };
        ExpectAnyIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, lostMidSpin),
            [SegmentBoundaryIssueKind.TimingDrift, SegmentBoundaryIssueKind.GeometryDrift],
            "dropping 999 midspin across cut cannot be certified");

        // An unknown event's roundtrip payload must not be silently changed,
        // even when that event is not modeled by TimingMapBuilder.
        LevelSegment[] alteredPayload = parts.ToArray();
        LevelAction[] secondActions = parts[1].Actions.ToArray();
        int eventIndex = Array.FindIndex(secondActions,
            a => a.EventType == "CustomBackground");
        Check(eventIndex >= 0, "fixture contains an unknown-payload event");
        LevelAction eventCopy = secondActions[eventIndex];
        JsonObject props = (JsonObject)eventCopy.SourceProperties!.DeepClone();
        props["custom"]!["nested"] = "changed";
        secondActions[eventIndex] = eventCopy with { SourceProperties = props };
        alteredPayload[1] = parts[1] with { Actions = secondActions };
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, alteredPayload),
            SegmentBoundaryIssueKind.EventDrift,
            "unknown event JSON is preserved, not ignored");

        // An anchor supplied in the *same* sample-rate units as the audio must
        // refer to the original floor entry (± one sample for rounding).
        SegmentBoundarySampleAnchor[] wrongSample =
        [
            anchors[0] with { EntrySample = anchors[0].EntrySample + 24 },
            anchors[1]
        ];
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                baseline, parts, wrongSample),
            SegmentBoundaryIssueKind.SampleAnchorMismatch,
            "a shifted sample anchor is rejected rather than re-timing content");

        // The current loader/timing model does not resolve persistent
        // ScaleRadius and PositionTrack motion across a cut. Passing because
        // timing alone matches would incorrectly certify the joined chart.
        LevelDocument scale = Chart();
        scale.ReplaceActions(scale.ActionStore.Actions.Append(
            Action(1, "ScaleRadius")));
        LevelSegment[] scaleParts = LevelSegmentPartitioner.Split(scale, 3, 6).ToArray();
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                scale, scaleParts),
            SegmentBoundaryIssueKind.UnsupportedCrossBoundaryEffect,
            "ScaleRadius crossing a Segment boundary is fail-closed");

        LevelDocument positionTrack = Chart();
        positionTrack.ReplaceActions(positionTrack.ActionStore.Actions.Append(
            Action(4, "PositionTrack")));
        LevelSegment[] trackParts =
            LevelSegmentPartitioner.Split(positionTrack, 3, 6).ToArray();
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                positionTrack, trackParts),
            SegmentBoundaryIssueKind.UnsupportedCrossBoundaryEffect,
            "PositionTrack crossing a Segment boundary is fail-closed");

        // Even an otherwise unchanged music chart cannot claim global
        // choreography correctness if a supported-as-raw timed event may
        // continue across the boundary.
        LevelDocument movingDecoration = Chart();
        movingDecoration.ReplaceActions(movingDecoration.ActionStore.Actions.Append(
            Action(2, "MoveDecorations") with
            {
                Duration = 3.0,
                SourceProperties = new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = "MoveDecorations",
                    ["duration"] = 3.0,
                    ["tag"] = "effects"
                }
            }));
        ExpectIssue(SegmentBoundaryValidator.ValidateRoundTrip(
                movingDecoration, LevelSegmentPartitioner.Split(movingDecoration, 3, 6)),
            SegmentBoundaryIssueKind.UnsupportedCrossBoundaryEffect,
            "cross-boundary timed decoration effect remains unresolved");

        // Invalid segments remain a caller error, not a positive validation.
        LevelSegment[] gap = parts.ToArray();
        gap[1] = gap[1] with { StartFloor = 4 };
        ExpectRejected(() => SegmentBoundaryValidator.ValidateRoundTrip(
            baseline, gap), "a gap in floor coverage cannot export");

        // This does not validate arbitrary audio-sample cuts through a floor,
        // stock .adofai rendering/export, or intentional edited-chart changes.
    }

    private static LevelDocument Chart()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(9);
        level.Angles = [0d, 45d, 999d, 90d, 270d, 15d, 120d, 315d];
        level.RebuildGeometry();
        level.InitialBpm = 120.0;
        level.CountdownTicks = 0;
        level.SeparateCountdownTime = false;
        level.OffsetMilliseconds = 125;
        level.PitchPercent = 90;
        level.ReplaceActions(
        [
            Action(1, "SetSpeed") with
            {
                SpeedType = "Bpm",
                BeatsPerMinute = 150
            },
            Action(2, "Twirl"),
            Action(2, "Pause") with { Duration = 1.25 },
            Action(4, "SetSpeed") with
            {
                SpeedType = "Multiplier",
                BpmMultiplier = 1.5
            },
            Action(5, "Twirl"),
            Action(6, "Pause") with { Duration = 0.5 },
            Action(4, "CustomBackground") with
            {
                SourceIndex = 100,
                Active = false,
                SourceProperties = new JsonObject
                {
                    ["floor"] = 4,
                    ["eventType"] = "CustomBackground",
                    ["custom"] = new JsonObject { ["nested"] = "original" }
                }
            }
        ]);
        return level;
    }

    private static LevelAction Action(int floor, string eventType) =>
        new(floor, eventType, true, null, null, null, null);

    private static SegmentBoundarySampleAnchor AtEntrySample(LevelDocument level, int floor)
    {
        TimingMap map = TimingMapBuilder.Build(level);
        double audioSeconds = PlaybackClock.ChartToAudioTime(level, map.GetEntryTime(floor));
        long sample = checked((long)Math.Round(audioSeconds * SampleRate,
            MidpointRounding.AwayFromZero));
        return new SegmentBoundarySampleAnchor(floor, SampleRate, sample);
    }

    private static LevelSegment[] ReplaceParts(
        LevelSegment[] parts, int index, LevelSegment replacement)
    {
        LevelSegment[] result = parts.ToArray();
        result[index] = replacement;
        return result;
    }

    private static void ExpectIssue(
        SegmentBoundaryValidationResult result,
        SegmentBoundaryIssueKind kind, string description)
    {
        if (result.CanExportModeledRoundTrip ||
            !result.Issues.Any(issue => issue.Kind == kind))
            throw new InvalidOperationException(
                $"Expected {kind} at boundary: {description}");
    }

    private static void ExpectAnyIssue(
        SegmentBoundaryValidationResult result,
        SegmentBoundaryIssueKind[] kinds, string description)
    {
        if (result.CanExportModeledRoundTrip ||
            !result.Issues.Any(issue => kinds.Contains(issue.Kind)))
            throw new InvalidOperationException(
                $"Expected timing or geometry rejection: {description}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private static void ExpectRejected(Action test, string description)
    {
        try
        {
            test();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Invalid Segment coverage accepted: {description}");
    }
}
