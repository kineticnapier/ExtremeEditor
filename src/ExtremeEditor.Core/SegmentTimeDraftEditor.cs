namespace ExtremeEditor.Core;

/// <summary>
/// A time-positioned draft marker for one future ADOFAI tile.
/// AudioSample is an absolute position on the shared decoded audio timeline.
/// It is NOT an ADOFAI floor, angle, or proven hit time after composition.
/// </summary>
public sealed record SegmentDraftTileAnchor(
    string TileId,
    long AudioSample);

/// <summary>
/// An isolated editable draft opened from an EMPTY time-owned Segment.
/// No floor or angle is invented until its musical entry/exit state can
/// actually be resolved in a separate composition phase.
/// </summary>
public sealed record SegmentTimeDraft(
    string SegmentId,
    string OwnerId,
    int SampleRate,
    long StartSample,
    long EndSampleExclusive,
    IReadOnlyList<SegmentDraftTileAnchor> TileAnchors)
{
    public bool IsEmpty => TileAnchors.Count == 0;
}

/// <summary>
/// In-memory authoring of music-time tile markers inside owned EMPTY
/// allocations. Does not synthesize ADOFAI floors or persist/publish drafts.
/// Each edit returns a fresh snapshot, retaining prior snapshots for Undo.
/// </summary>
public static class SegmentTimeDraftEditor
{
    public static SegmentTimeDraft Open(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentContentEntry> entries,
        string segmentId,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        // An absent entry means missing content, NOT an empty draft.
        // Validate the entire snapshot rather than silently ignoring
        // an unrelated author's broken allocation or data.
        SegmentContentValidationResult validation =
            SegmentContentPlanner.Validate(timeline, entries);
        if (!validation.Accepted)
            throw new InvalidOperationException(
                "Cannot open a draft from an invalid time/content snapshot: " +
                string.Join("; ", validation.Issues.Select(
                    issue => $"{issue.Kind} [{issue.SegmentId}]: {issue.Detail}")));

        SegmentTimePartition partition = timeline.Partitions.FirstOrDefault(
            item => string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                "The requested Segment does not exist.", nameof(segmentId));

        if (partition.Status != SegmentPartitionStatus.Active ||
            !string.Equals(partition.OwnerId, ownerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Only the owner of an active Segment may open it for editing.");

        if (!entries.TryGetValue(segmentId, out SegmentContentEntry? content) ||
            content is null || content.Kind != SegmentContentKind.Empty ||
            content.FloorAlignedContent is not null ||
            content.RevisionId is not null ||
            partition.ContentRevisionId is not null)
            throw new InvalidOperationException(
                "Only an explicitly empty Segment can be opened as a new time draft.");

        return new SegmentTimeDraft(
            partition.SegmentId,
            ownerId,
            timeline.SampleRate,
            partition.StartSample,
            partition.EndSampleExclusive,
            Array.AsReadOnly(Array.Empty<SegmentDraftTileAnchor>()));
    }

    public static SegmentTimeDraft PlaceAnchor(
        SegmentTimeDraft draft, string tileId, long audioSample)
    {
        ValidateDraft(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);

        if (audioSample < draft.StartSample ||
            audioSample >= draft.EndSampleExclusive)
            throw new ArgumentOutOfRangeException(nameof(audioSample),
                "The tile position must be inside its owned half-open sample interval.");

        if (draft.TileAnchors.Any(anchor =>
                string.Equals(anchor.TileId, tileId, StringComparison.Ordinal)))
            throw new ArgumentException(
                "Draft tile IDs must be unique within a Segment.", nameof(tileId));

        // Upper bound: equal-time anchors keep their existing insertion order.
        // The resulting array is copied and wrapped, so older Undo snapshots
        // cannot be modified through the public IReadOnlyList property.
        int lo = 0;
        int hi = draft.TileAnchors.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (draft.TileAnchors[mid].AudioSample <= audioSample)
                lo = mid + 1;
            else
                hi = mid;
        }

        var result = draft.TileAnchors.ToList();
        result.Insert(lo, new SegmentDraftTileAnchor(tileId, audioSample));
        return draft with { TileAnchors = Array.AsReadOnly(result.ToArray()) };
    }

    public static SegmentTimeDraft RemoveAnchor(
        SegmentTimeDraft draft, string tileId)
    {
        ValidateDraft(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(tileId);

        var result = draft.TileAnchors.ToList();
        int index = result.FindIndex(anchor =>
            string.Equals(anchor.TileId, tileId, StringComparison.Ordinal));
        if (index < 0)
            throw new ArgumentException(
                "The requested tile anchor does not exist.", nameof(tileId));

        result.RemoveAt(index);
        return draft with { TileAnchors = Array.AsReadOnly(result.ToArray()) };
    }

    private static void ValidateDraft(SegmentTimeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.SegmentId) ||
            string.IsNullOrWhiteSpace(draft.OwnerId) ||
            draft.SampleRate <= 0 ||
            draft.StartSample < 0 ||
            draft.EndSampleExclusive <= draft.StartSample ||
            draft.TileAnchors is null)
            throw new ArgumentException("The time draft has invalid metadata.", nameof(draft));

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        long previousSample = draft.StartSample;
        foreach (SegmentDraftTileAnchor? anchor in draft.TileAnchors)
        {
            if (anchor is null ||
                string.IsNullOrWhiteSpace(anchor.TileId) ||
                !usedIds.Add(anchor.TileId) ||
                anchor.AudioSample < draft.StartSample ||
                anchor.AudioSample >= draft.EndSampleExclusive ||
                anchor.AudioSample < previousSample)
                throw new ArgumentException(
                    "The draft contains an invalid, duplicate or unsorted tile anchor.",
                    nameof(draft));

            previousSample = anchor.AudioSample;
        }
    }
}
