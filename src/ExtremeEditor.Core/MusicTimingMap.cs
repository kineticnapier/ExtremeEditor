namespace ExtremeEditor.Core;

public sealed record MusicTimingPoint(
    long StartSample,
    decimal BeatsPerMinute);

public sealed record MusicTimingSection(
    int TimingPointIndex,
    long StartSample,
    long? EndSampleExclusive,
    decimal BeatsPerMinute);

public sealed record MusicTimingGridLine(
    long AudioSample,
    int TimingPointIndex,
    long LocalTickIndex,
    bool IsWholeBeat);

public sealed record MusicTimingSnap(
    long AudioSample,
    int TimingPointIndex,
    long LocalTickIndex);

/// <summary>
/// Ordered musical BPM sections. Timing points are not
/// ADOFAI SetSpeed events and do not claim playable floor timing.
/// </summary>
public sealed class MusicTimingMap
{
    private readonly MusicTimingPoint[] _timingPoints;
    private readonly MusicBeatGrid[] _grids;
    private readonly IReadOnlyList<MusicTimingPoint> _publishedTimingPoints;

    private MusicTimingMap(
        int sampleRate,
        MusicTimingPoint[] timingPoints,
        MusicBeatGrid[] grids)
    {
        SampleRate = sampleRate;
        _timingPoints = timingPoints;
        _grids = grids;
        _publishedTimingPoints = Array.AsReadOnly(timingPoints);
    }

    public int SampleRate { get; }
    public IReadOnlyList<MusicTimingPoint> TimingPoints =>
        _publishedTimingPoints;

    public static MusicTimingMap Build(
        int sampleRate,
        IReadOnlyList<MusicTimingPoint> timingPoints)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate),
                "The decoded-audio sample rate must be positive.");
        ArgumentNullException.ThrowIfNull(timingPoints);
        if (timingPoints.Count == 0)
            throw new ArgumentException(
                "At least one musical timing point is required.",
                nameof(timingPoints));

        var copied = new MusicTimingPoint[timingPoints.Count];
        var grids = new MusicBeatGrid[timingPoints.Count];
        long previousStart = 0;
        for (int i = 0; i < timingPoints.Count; i++)
        {
            MusicTimingPoint? source = timingPoints[i];
            if (source is null)
                throw new ArgumentException(
                    "A musical timing point cannot be null.",
                    nameof(timingPoints));
            if (source.BeatsPerMinute <= 0m)
                throw new ArgumentException(
                    "A musical timing point must have positive BPM.",
                    nameof(timingPoints));
            if (i > 0 && source.StartSample <= previousStart)
                throw new ArgumentException(
                    "Musical timing points must already be strictly ordered by sample.",
                    nameof(timingPoints));

            copied[i] = new MusicTimingPoint(
                source.StartSample, source.BeatsPerMinute);
            grids[i] = MusicBeatGrid.CreateConstant(
                sampleRate, source.BeatsPerMinute, source.StartSample);
            previousStart = source.StartSample;
        }

        return new MusicTimingMap(sampleRate, copied, grids);
    }

    public MusicTimingSection FindSection(long audioSample)
    {
        int index = FindSectionIndex(audioSample);
        MusicTimingPoint point = _timingPoints[index];
        return new MusicTimingSection(
            index,
            point.StartSample,
            index + 1 < _timingPoints.Length
                ? _timingPoints[index + 1].StartSample
                : null,
            point.BeatsPerMinute);
    }

    public MusicTimingSnap Snap(
        long audioSample,
        MusicBeatSubdivision subdivision)
    {
        int index = FindSectionIndex(audioSample);
        MusicBeatSnap local = _grids[index].Snap(audioSample, subdivision);
        long localTick = local.TickIndex;
        long localSample = local.AudioSample;
        long? sectionEnd = index + 1 < _timingPoints.Length
            ? _timingPoints[index + 1].StartSample
            : null;

        // A rounded line from the old BPM is not valid at or beyond the next
        // timing point. Walk to its preceding valid line before comparing the
        // new section's boundary.
        while (sectionEnd is long end && localSample >= end)
        {
            if (localTick == long.MinValue)
                throw new OverflowException("The musical timing tick overflowed.");
            localTick--;
            localSample = _grids[index].GetSample(localTick, subdivision);
        }

        var best = new MusicTimingSnap(localSample, index, localTick);
        if (sectionEnd is long nextBoundary)
        {
            var boundary = new MusicTimingSnap(nextBoundary, index + 1, 0);
            if (IsCloserOrLaterTie(boundary.AudioSample,
                    best.AudioSample, audioSample))
                best = boundary;
        }

        return best;
    }

    public IReadOnlyList<MusicTimingGridLine> GetVisibleLines(
        long startSample,
        long endSampleExclusive,
        MusicBeatSubdivision subdivision,
        int maximumLineCount = 10_000)
    {
        if (endSampleExclusive <= startSample)
            throw new ArgumentException(
                "The visible musical timing range must be nonempty.",
                nameof(endSampleExclusive));
        if (maximumLineCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLineCount),
                "The visible musical timing line budget must be positive.");

        var result = new List<MusicTimingGridLine>();
        int index = FindSectionIndex(startSample);
        long sectionStart = startSample;
        while (index < _timingPoints.Length &&
               sectionStart < endSampleExclusive)
        {
            long sectionEnd = index + 1 < _timingPoints.Length
                ? Math.Min(endSampleExclusive,
                    _timingPoints[index + 1].StartSample)
                : endSampleExclusive;
            if (sectionEnd > sectionStart)
            {
                IReadOnlyList<MusicBeatGridLine> lines =
                    _grids[index].GetVisibleLines(
                        sectionStart,
                        sectionEnd,
                        subdivision,
                        maximumLineCount);
                foreach (MusicBeatGridLine line in lines)
                {
                    if (result.Count == maximumLineCount)
                        throw new InvalidOperationException(
                            "The visible musical timing line budget was exceeded. Zoom in or raise the explicit limit.");
                    result.Add(new MusicTimingGridLine(
                        line.AudioSample,
                        index,
                        line.TickIndex,
                        line.IsWholeBeat));
                }
            }

            index++;
            if (index >= _timingPoints.Length)
                break;
            sectionStart = Math.Max(startSample, _timingPoints[index].StartSample);
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private int FindSectionIndex(long audioSample)
    {
        int lo = 0;
        int hi = _timingPoints.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_timingPoints[mid].StartSample <= audioSample)
                lo = mid + 1;
            else
                hi = mid;
        }

        return Math.Max(0, lo - 1);
    }

    private static bool IsCloserOrLaterTie(
        long candidate,
        long current,
        long requested)
    {
        System.Numerics.BigInteger candidateDistance =
            System.Numerics.BigInteger.Abs(
                (System.Numerics.BigInteger)candidate - requested);
        System.Numerics.BigInteger currentDistance =
            System.Numerics.BigInteger.Abs(
                (System.Numerics.BigInteger)current - requested);
        return candidateDistance < currentDistance ||
               candidateDistance == currentDistance && candidate > current;
    }
}
