namespace ExtremeEditor.Core;

/// <summary>
/// Working ownership of a half-open range on the common audio timeline.
/// Sample coordinates refer to the project's shared decoded audio timeline,
/// not to ADOFAI BPM/beats or chart-relative seconds.
/// A null ContentRevisionId denotes a newly created, still-empty segment.
/// </summary>
public sealed record SegmentTimePartition(
    string SegmentId,
    long StartSample,
    long EndSampleExclusive,
    string? ContentRevisionId,
    long ContentOriginSample,
    string? OwnerId,
    SegmentPartitionStatus Status)
{
    public long LengthSamples => EndSampleExclusive - StartSample;
}

public enum SegmentPartitionStatus : byte
{
    Active,
    Parked
}

/// <summary>
/// Immutable-by-convention project allocation snapshot. Audio sample rate is
/// project-wide and never inferred from SetSpeed, Twirl or other chart events.
/// </summary>
public sealed record SegmentTimeTimeline(
    int SampleRate,
    IReadOnlyList<SegmentTimePartition> Partitions);

/// <summary>
/// Reorganizes audio-timeline assignments without touching LevelDocument or
/// LevelSegment data. A split creates two views of the same immutable content
/// revision; content is neither shifted in time nor copied/sliced.
/// </summary>
public static class SegmentTimePlanner
{
    public static void Validate(SegmentTimeTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("The common audio sample rate must be positive.", nameof(timeline));
        if (timeline.Partitions is null)
            throw new ArgumentException("The timeline must provide a partition collection.", nameof(timeline));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<SegmentTimePartition>();
        foreach (SegmentTimePartition? partition in timeline.Partitions)
        {
            if (partition is null)
                throw new ArgumentException("A partition cannot be null.", nameof(timeline));
            if (string.IsNullOrWhiteSpace(partition.SegmentId) || !ids.Add(partition.SegmentId))
                throw new ArgumentException("Segment IDs must be nonempty and unique.", nameof(timeline));
            if (partition.StartSample < 0 || partition.EndSampleExclusive <= partition.StartSample)
                throw new ArgumentException(
                    $"Segment '{partition.SegmentId}' has an invalid half-open range.", nameof(timeline));
            if (partition.ContentOriginSample < 0 ||
                partition.ContentOriginSample > partition.StartSample)
                throw new ArgumentException(
                    $"Segment '{partition.SegmentId}' has an invalid content origin.", nameof(timeline));

            if (partition.Status == SegmentPartitionStatus.Active)
            {
                if (string.IsNullOrWhiteSpace(partition.OwnerId))
                    throw new ArgumentException(
                        $"Active segment '{partition.SegmentId}' has no owner.", nameof(timeline));
                active.Add(partition);
            }
            else if (partition.Status == SegmentPartitionStatus.Parked)
            {
                if (partition.OwnerId is not null)
                    throw new ArgumentException(
                        $"Parked segment '{partition.SegmentId}' must be unassigned.", nameof(timeline));
            }
            else
            {
                throw new ArgumentException(
                    $"Segment '{partition.SegmentId}' has an unknown status.", nameof(timeline));
            }
        }

        // A parked revision is retained but does not reserve its old time range.
        // Adjacent [a,b) and [b,c) active segments are legal; overlap is not.
        long previousEnd = 0;
        foreach (SegmentTimePartition partition in active
                     .OrderBy(static part => part.StartSample)
                     .ThenBy(static part => part.EndSampleExclusive))
        {
            if (partition.StartSample < previousEnd)
                throw new ArgumentException(
                    $"Active segment '{partition.SegmentId}' overlaps another assignment.",
                    nameof(timeline));
            previousEnd = partition.EndSampleExclusive;
        }
    }

    public static SegmentTimeTimeline SplitAt(
        SegmentTimeTimeline timeline, string segmentId, long sample, string rightSegmentId)
    {
        Validate(timeline);
        CheckIdentifier(segmentId, nameof(segmentId));
        CheckIdentifier(rightSegmentId, nameof(rightSegmentId));
        if (timeline.Partitions.Any(part => string.Equals(
                part.SegmentId, rightSegmentId, StringComparison.Ordinal)))
            throw new ArgumentException("The new right-hand segment ID already exists.", nameof(rightSegmentId));

        int index = FindIndex(timeline, segmentId);
        SegmentTimePartition original = timeline.Partitions[index];
        if (original.Status != SegmentPartitionStatus.Active)
            throw new InvalidOperationException("Only an active assignment can be split.");
        if (sample <= original.StartSample || sample >= original.EndSampleExclusive)
            throw new ArgumentException(
                "The split point must be strictly inside the segment's range.", nameof(sample));

        var result = timeline.Partitions.ToList();
        // Both sides refer to the original content revision and audio origin.
        // No note or event is modified, and no musical state is inferred.
        result[index] = original with { EndSampleExclusive = sample };
        result.Insert(index + 1, original with
        {
            SegmentId = rightSegmentId,
            StartSample = sample
        });
        return CheckedSnapshot(timeline.SampleRate, result);
    }

    public static SegmentTimeTimeline CreateEmpty(
        SegmentTimeTimeline timeline, string segmentId,
        long startSample, long endSampleExclusive, string ownerId)
    {
        Validate(timeline);
        CheckIdentifier(segmentId, nameof(segmentId));
        CheckIdentifier(ownerId, nameof(ownerId));
        if (startSample < 0 || endSampleExclusive <= startSample)
            throw new ArgumentException("New segment range must be nonempty and nonnegative.", nameof(startSample));
        if (timeline.Partitions.Any(part => string.Equals(
                part.SegmentId, segmentId, StringComparison.Ordinal)))
            throw new ArgumentException("The new segment ID already exists.", nameof(segmentId));

        foreach (SegmentTimePartition part in timeline.Partitions)
        {
            if (part.Status == SegmentPartitionStatus.Active &&
                startSample < part.EndSampleExclusive &&
                part.StartSample < endSampleExclusive)
            {
                throw new InvalidOperationException(
                    $"New segment '{segmentId}' overlaps active segment '{part.SegmentId}'.");
            }
        }

        var result = timeline.Partitions.ToList();
        result.Add(new SegmentTimePartition(
            segmentId,
            startSample,
            endSampleExclusive,
            ContentRevisionId: null,
            ContentOriginSample: startSample,
            OwnerId: ownerId,
            Status: SegmentPartitionStatus.Active));
        return CheckedSnapshot(timeline.SampleRate, result);
    }

    public static SegmentTimeTimeline Park(SegmentTimeTimeline timeline, string segmentId)
    {
        Validate(timeline);
        CheckIdentifier(segmentId, nameof(segmentId));
        int index = FindIndex(timeline, segmentId);
        SegmentTimePartition original = timeline.Partitions[index];
        if (original.Status != SegmentPartitionStatus.Active)
            throw new InvalidOperationException("Only an active segment can be parked.");

        var result = timeline.Partitions.ToList();
        result[index] = original with
        {
            Status = SegmentPartitionStatus.Parked,
            OwnerId = null
        };
        return CheckedSnapshot(timeline.SampleRate, result);
    }

    private static SegmentTimeTimeline CheckedSnapshot(
        int sampleRate, List<SegmentTimePartition> partitions)
    {
        // Expose an independent array, never mutate the caller's original list.
        var snapshot = new SegmentTimeTimeline(sampleRate, partitions.ToArray());
        Validate(snapshot);
        return snapshot;
    }

    private static int FindIndex(SegmentTimeTimeline timeline, string segmentId)
    {
        for (int i = 0; i < timeline.Partitions.Count; i++)
        {
            if (string.Equals(timeline.Partitions[i].SegmentId, segmentId, StringComparison.Ordinal))
                return i;
        }
        throw new ArgumentException($"Unknown segment ID: '{segmentId}'.", nameof(segmentId));
    }

    private static void CheckIdentifier(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Identifier cannot be empty.", parameter);
    }
}
