namespace ExtremeEditor.Core;

/// <summary>
/// A segment's authored-content state is separate from the audio-time allocation.
/// Empty is an explicit, legitimate payload state, not a missing dictionary key.
/// FloorAligned represents existing PoC payloads; future time-first authored
/// content need not be limited to a floor-aligned LevelSegment.
/// </summary>
public enum SegmentContentKind : byte
{
    Empty,
    FloorAligned,
    TimeAuthored
}

/// <summary>
/// An explicit content slot for one stable SegmentId. An empty segment owns
/// no chart tiles and needs no invented floor or entry angle.
/// RevisionId must agree with its audio-time partition.
/// </summary>
public sealed record SegmentContentEntry(
    string SegmentId,
    SegmentContentKind Kind,
    string? RevisionId,
    LevelSegment? FloorAlignedContent,
    TimeFirstAuthoredSegment? TimeAuthoredContent = null);

public enum SegmentContentIssueKind : byte
{
    InvalidTimeline,
    MissingActiveEntry,
    InvalidContentEntry,
    RevisionMismatch,
    UnknownAllocation
}

public sealed record SegmentContentIssue(
    SegmentContentIssueKind Kind,
    string? SegmentId,
    string Detail);

public sealed record SegmentContentValidationResult(
    IReadOnlyList<SegmentContentIssue> Issues)
{
    public bool Accepted => Issues.Count == 0;
}

/// <summary>
/// Validates explicitly empty and authored content for audio-time assignments.
/// This is draft-content validation only: it does not merge or export charts,
/// persist Git snapshots, invent ADOFAI floors or map samples to floor cuts.
/// </summary>
public static class SegmentContentPlanner
{
    public static SegmentContentEntry CreateEmpty(SegmentTimePartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.Status != SegmentPartitionStatus.Active ||
            string.IsNullOrWhiteSpace(partition.SegmentId) ||
            string.IsNullOrWhiteSpace(partition.OwnerId) ||
            partition.StartSample < 0 ||
            partition.EndSampleExclusive <= partition.StartSample ||
            partition.ContentOriginSample < 0 ||
            partition.ContentOriginSample > partition.StartSample)
        {
            throw new ArgumentException(
                "An empty content entry requires a valid, owned, active audio-time allocation.",
                nameof(partition));
        }

        // A revision reference is evidence that authored content existed;
        // never quietly replace it with an 'empty' marker.
        if (partition.ContentRevisionId is not null)
            throw new InvalidOperationException(
                "An allocation with a content revision cannot be marked empty.");

        return new SegmentContentEntry(
            partition.SegmentId, SegmentContentKind.Empty, null, null);
    }

    public static SegmentContentValidationResult Validate(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentContentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(entries);

        try
        {
            SegmentTimePlanner.Validate(timeline);
        }
        catch (ArgumentException ex)
        {
            return new SegmentContentValidationResult(
            [
                new SegmentContentIssue(
                    SegmentContentIssueKind.InvalidTimeline, null, ex.Message)
            ]);
        }

        var issues = new List<SegmentContentIssue>();
        var byId = timeline.Partitions.ToDictionary(
            static partition => partition.SegmentId, StringComparer.Ordinal);

        foreach (SegmentTimePartition partition in timeline.Partitions)
        {
            // Parked content is optional: its old revision may live elsewhere.
            // An active authoring region must have an explicit content slot.
            if (!entries.TryGetValue(partition.SegmentId, out SegmentContentEntry? entry))
            {
                if (partition.Status == SegmentPartitionStatus.Active)
                    issues.Add(new(
                        SegmentContentIssueKind.MissingActiveEntry,
                        partition.SegmentId,
                        "The active allocation has no explicit content entry."));
                continue;
            }

            if (entry is null ||
                !string.Equals(entry.SegmentId, partition.SegmentId,
                    StringComparison.Ordinal))
            {
                issues.Add(new(
                    SegmentContentIssueKind.InvalidContentEntry,
                    partition.SegmentId,
                    "The content entry is null or its SegmentId differs from the allocation."));
                continue;
            }

            if (entry.Kind == SegmentContentKind.Empty)
            {
                if (entry.FloorAlignedContent is not null ||
                    entry.TimeAuthoredContent is not null)
                    issues.Add(new(
                        SegmentContentIssueKind.InvalidContentEntry,
                        partition.SegmentId,
                        "An empty content entry cannot contain floor or tile data."));

                if (partition.ContentRevisionId is not null || entry.RevisionId is not null)
                    issues.Add(new(
                        SegmentContentIssueKind.RevisionMismatch,
                        partition.SegmentId,
                        "An empty entry cannot replace a referenced content revision."));
            }
            else if (entry.Kind == SegmentContentKind.FloorAligned)
            {
                if (entry.FloorAlignedContent is not LevelSegment payload ||
                    !IsValidFloorPayload(payload) ||
                    entry.TimeAuthoredContent is not null)
                {
                    issues.Add(new(
                        SegmentContentIssueKind.InvalidContentEntry,
                        partition.SegmentId,
                        "Floor-aligned content is missing or structurally invalid."));
                }

                if (string.IsNullOrWhiteSpace(partition.ContentRevisionId) ||
                    string.IsNullOrWhiteSpace(entry.RevisionId) ||
                    !string.Equals(entry.RevisionId, partition.ContentRevisionId,
                        StringComparison.Ordinal))
                {
                    issues.Add(new(
                        SegmentContentIssueKind.RevisionMismatch,
                        partition.SegmentId,
                        "Authored content must refer to its exact allocated revision."));
                }
            }
            else if (entry.Kind == SegmentContentKind.TimeAuthored)
            {
                if (entry.FloorAlignedContent is not null ||
                    entry.TimeAuthoredContent is not TimeFirstAuthoredSegment authored)
                {
                    issues.Add(new(
                        SegmentContentIssueKind.InvalidContentEntry,
                        partition.SegmentId,
                        "Time-authored content is missing or mixed with a floor payload."));
                }
                else
                {
                    try
                    {
                        TimeFirstSegmentEditorSession.Validate(authored);
                        if (!string.Equals(authored.SegmentId, partition.SegmentId,
                                StringComparison.Ordinal) ||
                            !string.Equals(authored.OwnerId, partition.OwnerId,
                                StringComparison.Ordinal) ||
                            authored.SampleRate != timeline.SampleRate ||
                            authored.StartSample != partition.StartSample ||
                            authored.EndSampleExclusive != partition.EndSampleExclusive)
                            throw new ArgumentException(
                                "Time-authored metadata differs from its allocation.");
                    }
                    catch (ArgumentException ex)
                    {
                        issues.Add(new(
                            SegmentContentIssueKind.InvalidContentEntry,
                            partition.SegmentId,
                            ex.Message));
                    }
                }

                if (string.IsNullOrWhiteSpace(partition.ContentRevisionId) ||
                    string.IsNullOrWhiteSpace(entry.RevisionId) ||
                    !string.Equals(entry.RevisionId, partition.ContentRevisionId,
                        StringComparison.Ordinal) ||
                    entry.TimeAuthoredContent is null ||
                    !string.Equals(entry.TimeAuthoredContent.RevisionId,
                        partition.ContentRevisionId, StringComparison.Ordinal))
                {
                    issues.Add(new(
                        SegmentContentIssueKind.RevisionMismatch,
                        partition.SegmentId,
                        "Time-authored content must refer to its exact allocated revision."));
                }
            }
            else
            {
                issues.Add(new(
                    SegmentContentIssueKind.InvalidContentEntry,
                    partition.SegmentId,
                    "Unknown Segment content kind."));
            }
        }

        // Check dictionary entries in addition to active allocations:
        // otherwise an orphan payload could be silently published or retained.
        foreach (string id in entries.Keys)
        {
            if (!byId.ContainsKey(id))
                issues.Add(new(
                    SegmentContentIssueKind.UnknownAllocation,
                    id,
                    "The content entry has no matching active or parked allocation."));
        }

        return new SegmentContentValidationResult(issues.ToArray());
    }

    private static bool IsValidFloorPayload(LevelSegment segment)
    {
        if (segment.StartFloor < 0 ||
            segment.EndFloorExclusive <= segment.StartFloor ||
            segment.DestinationAngles is null ||
            segment.Actions is null ||
            segment.Decorations is null)
            return false;

        int expectedAngles = segment.FloorCount - (segment.StartFloor == 0 ? 1 : 0);
        return segment.DestinationAngles.Length == expectedAngles &&
               segment.Actions.All(action =>
                   action is not null && action.Floor >= 0 &&
                   action.Floor < segment.FloorCount) &&
               segment.Decorations.All(decoration =>
                   decoration is not null && decoration.Floor is int floor &&
                   floor >= 0 && floor < segment.FloorCount);
    }
}
