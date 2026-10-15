using System.Numerics;

namespace ExtremeEditor.Core;

/// <summary>
/// Rejection reasons for an independently edited floor-aligned chart.
/// Acceptance is not a guarantee of stock-game parity or .adofai export.
/// </summary>
public enum SegmentEditedCompositionIssueKind : byte
{
    MissingActiveContent,
    InvalidFloorCoverage,
    SampleRangeMismatch,
    UnsupportedCrossBoundaryEffect,
    InvalidModeledState
}

public sealed record SegmentEditedCompositionIssue(
    SegmentEditedCompositionIssueKind Kind,
    string? SegmentId,
    int Floor,
    string Detail);

/// <summary>
/// Rejections contain no partially assembled candidate. The supplied chart,
/// allocations and per-author content are never modified by validation.
/// </summary>
public sealed record SegmentEditedCompositionResult(
    LevelDocument? Document,
    TimingMap? Timing,
    IReadOnlyList<SegmentEditedCompositionIssue> Issues)
{
    public bool Accepted =>
        Document is not null && Timing is not null && Issues.Count == 0;
}

/// <summary>
/// Composes the independently EDITED active floor Segments using the template
/// only for global chart settings and floor-less decorations. Computes one
/// global timing map, then checks every allocation's exact sample boundaries.
/// Parked revisions are retained by the caller but are not part of the chart.
/// This is not an arbitrary-time or mid-floor Segment composer.
/// </summary>
public static class SegmentEditedCompositionValidator
{
    private const double SampleTolerance = 1.0;

    public static SegmentEditedCompositionResult ComposeAndValidate(
        LevelDocument template,
        SegmentTimeTimeline allocations,
        IReadOnlyDictionary<string, LevelSegment> contentBySegmentId)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(contentBySegmentId);

        var issues = new List<SegmentEditedCompositionIssue>();
        try
        {
            SegmentTimePlanner.Validate(allocations);
        }
        catch (ArgumentException ex)
        {
            return Rejected(new SegmentEditedCompositionIssue(
                SegmentEditedCompositionIssueKind.InvalidModeledState,
                null, -1, "Invalid audio sample allocation: " + ex.Message));
        }

        SegmentTimePartition[] active = allocations.Partitions
            .Where(static partition => partition.Status == SegmentPartitionStatus.Active)
            .OrderBy(static partition => partition.StartSample)
            .ThenBy(static partition => partition.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (active.Length == 0)
        {
            return Rejected(new SegmentEditedCompositionIssue(
                SegmentEditedCompositionIssueKind.InvalidFloorCoverage,
                null, -1, "At least one active floor-aligned Segment is required."));
        }

        var allocationsById = allocations.Partitions
            .Select(static p => p.SegmentId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in contentBySegmentId.Keys)
        {
            if (!allocationsById.Contains(id))
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidModeledState,
                    id, -1, "The content has no matching active or parked allocation."));
        }

        var orderedSegments = new List<(SegmentTimePartition Partition, LevelSegment Segment)>();
        foreach (SegmentTimePartition partition in active)
        {
            if (!contentBySegmentId.TryGetValue(partition.SegmentId, out LevelSegment? segment) ||
                segment is null)
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.MissingActiveContent,
                    partition.SegmentId, -1,
                    "An active time assignment has no floor-aligned content."));
                continue;
            }
            orderedSegments.Add((partition, segment));
        }

        if (issues.Count != 0)
            return Rejected(issues);

        // Audio assignments do NOT define arbitrary tile cuts. Floor slices
        // must already be contiguous and strictly ordered in the shared chart.
        // The right Segment owns the angle entering its first floor.
        int nextFloor = 0;
        foreach ((SegmentTimePartition partition, LevelSegment segment) in orderedSegments)
        {
            if (segment.StartFloor != nextFloor ||
                segment.EndFloorExclusive <= segment.StartFloor ||
                segment.DestinationAngles is null ||
                segment.DestinationAngles.Length !=
                    segment.FloorCount - (segment.StartFloor == 0 ? 1 : 0) ||
                segment.Actions is null || segment.Decorations is null)
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidFloorCoverage,
                    partition.SegmentId, segment.StartFloor,
                    "An active Segment has invalid, missing or overlapping floor coverage."));
                break;
            }
            nextFloor = segment.EndFloorExclusive;
        }
        if (issues.Count == 0 && nextFloor != template.FloorCount)
        {
            issues.Add(new(
                SegmentEditedCompositionIssueKind.InvalidFloorCoverage,
                active[^1].SegmentId, nextFloor,
                "Active floor ranges must cover the template's current floor count exactly."));
        }
        if (issues.Count != 0)
            return Rejected(issues);

        LevelDocument candidate;
        try
        {
            candidate = LevelSegmentPartitioner.Merge(
                template, orderedSegments.Select(static item => item.Segment).ToArray());
        }
        catch (ArgumentException ex)
        {
            return Rejected(new SegmentEditedCompositionIssue(
                SegmentEditedCompositionIssueKind.InvalidFloorCoverage,
                null, -1, "The edited floor Segments could not be assembled: " + ex.Message));
        }

        // The fallback geometry builder does not evaluate these persistent
        // effects. Reject them conservatively if active before a cut. Unknown
        // timed actions can influence later content even if floor times match.
        int[] boundaries = orderedSegments.Skip(1)
            .Select(static item => item.Segment.StartFloor).ToArray();
        foreach (LevelAction action in candidate.ActionStore.Actions)
        {
            if (!action.Active)
                continue;

            if (action.Kind != LevelActionKinds.FromEventType(action.EventType))
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidModeledState,
                    FindSegmentId(orderedSegments, action.Floor),
                    action.Floor,
                    "Action kind no longer matches its event type."));
                continue;
            }

            if (boundaries.Any(boundary => action.Floor < boundary) &&
                (action.EventType is "ScaleRadius" or "PositionTrack" ||
                 (action.Kind == LevelActionKind.Unknown &&
                  (action.Duration is > 0 || action.EventType == "MoveDecorations"))))
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.UnsupportedCrossBoundaryEffect,
                    FindSegmentId(orderedSegments, action.Floor),
                    action.Floor,
                    "Unsupported event may modify playback/geometry across a Segment boundary: " +
                    action.EventType));
            }

            if (action.Kind == LevelActionKind.SetSpeed)
            {
                bool multiplier = string.Equals(
                    action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase);
                bool valid = multiplier
                    ? action.BpmMultiplier is double factor &&
                      double.IsFinite(factor) && factor > 0
                    : action.BeatsPerMinute is double bpm &&
                      double.IsFinite(bpm) && bpm > 0;
                if (!valid)
                    issues.Add(new(
                        SegmentEditedCompositionIssueKind.InvalidModeledState,
                        FindSegmentId(orderedSegments, action.Floor),
                        action.Floor, "SetSpeed must have a finite positive BPM or multiplier."));
            }
            else if (action.Kind == LevelActionKind.Pause &&
                     action.Duration is double pause &&
                     (!double.IsFinite(pause) || pause < 0))
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidModeledState,
                    FindSegmentId(orderedSegments, action.Floor),
                    action.Floor, "Pause has an invalid beat duration."));
            }
        }

        for (int i = 0; i < candidate.Angles.Length; i++)
        {
            if (!double.IsFinite(candidate.Angles[i]))
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidModeledState,
                    FindSegmentId(orderedSegments, i + 1), i + 1,
                    "Incoming destination angle is not finite."));
        }

        if (candidate.Positions.Any(static position =>
                !float.IsFinite(position.X) || !float.IsFinite(position.Y)))
        {
            issues.Add(new(
                SegmentEditedCompositionIssueKind.InvalidModeledState,
                null, -1, "Reconstructed world geometry contains non-finite positions."));
        }

        if (!double.IsFinite(candidate.InitialBpm) || candidate.InitialBpm <= 0 ||
            !double.IsFinite(candidate.OffsetMilliseconds) ||
            !double.IsFinite(candidate.PitchPercent) || candidate.PitchPercent <= 0)
        {
            issues.Add(new(
                SegmentEditedCompositionIssueKind.InvalidModeledState,
                null, -1, "Project-wide BPM, offset or pitch is not a valid clock setting."));
        }

        if (issues.Count != 0)
            return Rejected(issues);

        TimingMap timing = TimingMapBuilder.Build(candidate);
        foreach (FloorTiming floor in timing.Floors)
        {
            if (!double.IsFinite(floor.EntryTime) ||
                !double.IsFinite(floor.ExitTime) ||
                !double.IsFinite(floor.Bpm) || floor.Bpm <= 0 ||
                floor.ExitTime < floor.EntryTime ||
                !double.IsFinite(floor.AngleMoved) ||
                !double.IsFinite(floor.PauseSeconds))
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.InvalidModeledState,
                    FindSegmentId(orderedSegments, floor.Floor), floor.Floor,
                    "The globally recomputed playback timing is invalid."));
            }
        }
        if (issues.Count != 0)
            return Rejected(issues);

        // Compare each active assignment to the GLOBAL assembled chart, never
        // to an isolated Segment's local BPM/timing. Both endpoints are
        // decoded audio sample coordinates, after Offset/Pitch/Countdown.
        foreach ((SegmentTimePartition partition, LevelSegment segment) in orderedSegments)
        {
            double startSeconds = PlaybackClock.ChartToAudioTime(
                candidate, timing.GetEntryTime(segment.StartFloor));
            double endSeconds = PlaybackClock.ChartToAudioTime(
                candidate, segment.EndFloorExclusive < candidate.FloorCount
                    ? timing.GetEntryTime(segment.EndFloorExclusive)
                    : timing.Duration);
            double startSamples = startSeconds * allocations.SampleRate;
            double endSamples = endSeconds * allocations.SampleRate;

            if (!double.IsFinite(startSamples) || !double.IsFinite(endSamples) ||
                startSamples < 0 || endSamples <= startSamples ||
                Math.Abs(startSamples - partition.StartSample) > SampleTolerance ||
                Math.Abs(endSamples - partition.EndSampleExclusive) > SampleTolerance)
            {
                issues.Add(new(
                    SegmentEditedCompositionIssueKind.SampleRangeMismatch,
                    partition.SegmentId, segment.StartFloor,
                    $"Edited floor interval [{segment.StartFloor},{segment.EndFloorExclusive}) " +
                    "does not match its assigned audio sample interval."));
            }
        }

        return issues.Count == 0
            ? new SegmentEditedCompositionResult(
                candidate, timing, Array.Empty<SegmentEditedCompositionIssue>())
            : Rejected(issues);
    }

    private static string? FindSegmentId(
        IReadOnlyList<(SegmentTimePartition Partition, LevelSegment Segment)> ordered, int floor)
    {
        foreach (var item in ordered)
        {
            if (floor >= item.Segment.StartFloor && floor < item.Segment.EndFloorExclusive)
                return item.Partition.SegmentId;
        }
        return null;
    }

    private static SegmentEditedCompositionResult Rejected(
        SegmentEditedCompositionIssue issue) => Rejected(new[] { issue });

    private static SegmentEditedCompositionResult Rejected(
        IEnumerable<SegmentEditedCompositionIssue> issues) =>
        new(null, null, issues.ToArray());
}
