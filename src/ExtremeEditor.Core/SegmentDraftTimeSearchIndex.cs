namespace ExtremeEditor.Core;

/// <summary>
/// An exact song-sample query in an independently authored time Segment.
/// SegmentId is null only when the sample is outside every active allocation.
/// A non-null SegmentId with zero matches means an assigned section has no
/// tile marker at this sample. Markers are not playable ADOFAI floor events.
/// </summary>
public sealed record SegmentDraftTimeMatch(
    string? SegmentId,
    IReadOnlyList<SegmentDraftTileAnchor> Anchors)
{
    public bool IsAssigned => SegmentId is not null;
    public bool HasAnchors => Anchors.Count > 0;
}

/// <summary>
/// Immutable, pre-indexed lookup over active time-first draft Segments.
/// Building an index validates and freezes sorted allocations and per-Segment
/// monotone audio-sample markers; queries must not merge across boundaries.
/// </summary>
public sealed class SegmentDraftTimeSearchIndex
{
    private static readonly IReadOnlyList<SegmentDraftTileAnchor> NoAnchors =
        Array.AsReadOnly(Array.Empty<SegmentDraftTileAnchor>());

    private readonly IndexedSegment[] _segments;
    private readonly IReadOnlyDictionary<string, IndexedSegment> _segmentsById;

    private SegmentDraftTimeSearchIndex(IndexedSegment[] segments)
    {
        _segments = segments;
        _segmentsById = new System.Collections.ObjectModel.ReadOnlyDictionary<
            string, IndexedSegment>(segments.ToDictionary(
                static segment => segment.SegmentId,
                StringComparer.Ordinal));
    }

    public static SegmentDraftTimeSearchIndex Build(
        SegmentTimeTimeline timeline,
        IReadOnlyDictionary<string, SegmentTimeDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(drafts);
        SegmentTimePlanner.Validate(timeline);

        SegmentTimePartition[] active = timeline.Partitions
            .Where(static partition =>
                partition.Status == SegmentPartitionStatus.Active)
            .OrderBy(static partition => partition.StartSample)
            .ThenBy(static partition => partition.EndSampleExclusive)
            .ToArray();

        var indexed = new IndexedSegment[active.Length];
        for (int i = 0; i < active.Length; i++)
        {
            SegmentTimePartition partition = active[i];
            if (partition.ContentRevisionId is not null)
                throw new InvalidOperationException(
                    $"Active Segment '{partition.SegmentId}' is not an empty draft allocation.");

            if (!drafts.TryGetValue(partition.SegmentId, out SegmentTimeDraft? draft) ||
                draft is null)
            {
                throw new InvalidOperationException(
                    $"Active Segment '{partition.SegmentId}' has no submitted draft.");
            }

            ValidateDraft(timeline, partition, draft);
            var anchors = new SegmentDraftTileAnchor[draft.TileAnchors.Count];
            for (int anchorIndex = 0; anchorIndex < anchors.Length; anchorIndex++)
            {
                SegmentDraftTileAnchor source = draft.TileAnchors[anchorIndex];
                anchors[anchorIndex] = new SegmentDraftTileAnchor(
                    source.TileId, source.AudioSample);
            }

            indexed[i] = new IndexedSegment(
                partition.SegmentId,
                partition.StartSample,
                partition.EndSampleExclusive,
                anchors);
        }

        return new SegmentDraftTimeSearchIndex(indexed);
    }

    /// <summary>
    /// Look up the containing active Segment, then use lower/upper bounds in
    /// that Segment only. An unallocated gap returns SegmentId = null.
    /// </summary>
    public SegmentDraftTimeMatch FindAt(long audioSample)
    {
        int segmentIndex = UpperBoundByStart(audioSample) - 1;
        if (segmentIndex < 0 ||
            audioSample >= _segments[segmentIndex].EndSampleExclusive)
        {
            return new SegmentDraftTimeMatch(null, NoAnchors);
        }

        IndexedSegment segment = _segments[segmentIndex];
        int first = LowerBound(segment.Anchors, audioSample);
        int end = UpperBound(segment.Anchors, audioSample);
        return new SegmentDraftTimeMatch(
            segment.SegmentId,
            CopyRange(segment.Anchors, first, end));
    }

    /// <summary>
    /// Search [start,end) within exactly one Segment. Crossing even one
    /// allocation boundary is a contract violation, not an implicit merge.
    /// </summary>
    public IReadOnlyList<SegmentDraftTileAnchor> FindRange(
        string segmentId, long startSample, long endSampleExclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (!_segmentsById.TryGetValue(segmentId, out IndexedSegment? segment))
            throw new ArgumentException(
                "The requested active Segment is not indexed.", nameof(segmentId));
        if (endSampleExclusive <= startSample)
            throw new ArgumentException(
                "The search range must be a nonempty half-open interval.",
                nameof(endSampleExclusive));
        if (startSample < segment.StartSample ||
            endSampleExclusive > segment.EndSampleExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(startSample),
                "The search range must stay entirely inside the requested Segment.");
        }

        int first = LowerBound(segment.Anchors, startSample);
        int end = LowerBound(segment.Anchors, endSampleExclusive);
        return CopyRange(segment.Anchors, first, end);
    }

    private static void ValidateDraft(
        SegmentTimeTimeline timeline,
        SegmentTimePartition partition,
        SegmentTimeDraft draft)
    {
        if (!string.Equals(draft.SegmentId, partition.SegmentId,
                StringComparison.Ordinal) ||
            !string.Equals(draft.OwnerId, partition.OwnerId,
                StringComparison.Ordinal) ||
            draft.SampleRate != timeline.SampleRate ||
            draft.StartSample != partition.StartSample ||
            draft.EndSampleExclusive != partition.EndSampleExclusive ||
            draft.TileAnchors is null)
        {
            throw new InvalidOperationException(
                $"Draft metadata for Segment '{partition.SegmentId}' does not match its active allocation.");
        }

        long previousSample = partition.StartSample;
        var tileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SegmentDraftTileAnchor? anchor in draft.TileAnchors)
        {
            if (anchor is null ||
                string.IsNullOrWhiteSpace(anchor.TileId) ||
                anchor.AudioSample < partition.StartSample ||
                anchor.AudioSample >= partition.EndSampleExclusive ||
                anchor.AudioSample < previousSample ||
                !tileIds.Add(anchor.TileId))
            {
                throw new ArgumentException(
                    $"Draft for Segment '{partition.SegmentId}' has an invalid, duplicate or unsorted tile anchor.",
                    nameof(draft));
            }

            previousSample = anchor.AudioSample;
        }
    }

    private int UpperBoundByStart(long sample)
    {
        int lo = 0;
        int hi = _segments.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_segments[mid].StartSample <= sample)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static int LowerBound(
        SegmentDraftTileAnchor[] anchors, long sample)
    {
        int lo = 0;
        int hi = anchors.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (anchors[mid].AudioSample < sample)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static int UpperBound(
        SegmentDraftTileAnchor[] anchors, long sample)
    {
        int lo = 0;
        int hi = anchors.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (anchors[mid].AudioSample <= sample)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static IReadOnlyList<SegmentDraftTileAnchor> CopyRange(
        SegmentDraftTileAnchor[] anchors, int first, int end)
    {
        int count = end - first;
        if (count == 0)
            return NoAnchors;

        var result = new SegmentDraftTileAnchor[count];
        Array.Copy(anchors, first, result, 0, count);
        return Array.AsReadOnly(result);
    }

    private sealed record IndexedSegment(
        string SegmentId,
        long StartSample,
        long EndSampleExclusive,
        SegmentDraftTileAnchor[] Anchors);
}
