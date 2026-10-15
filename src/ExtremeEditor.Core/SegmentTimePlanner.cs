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
/// RED contract for reorganizing independently edited segments in time.
/// Cutting a partition changes ownership metadata only: it does not slice
/// the underlying LevelSegment content or alter its timestamp origin.
/// </summary>
public static class SegmentTimePlanner
{
    public static void Validate(SegmentTimeTimeline timeline) =>
        throw new NotImplementedException("RED: validate half-open audio-sample partitions");

    public static SegmentTimeTimeline SplitAt(
        SegmentTimeTimeline timeline, string segmentId, long sample, string rightSegmentId) =>
        throw new NotImplementedException("RED: split time partition non-destructively");

    public static SegmentTimeTimeline CreateEmpty(
        SegmentTimeTimeline timeline, string segmentId,
        long startSample, long endSampleExclusive, string ownerId) =>
        throw new NotImplementedException("RED: add an independent empty segment in an unallocated range");

    public static SegmentTimeTimeline Park(
        SegmentTimeTimeline timeline, string segmentId) =>
        throw new NotImplementedException("RED: park an old range without discarding its content reference");
}
