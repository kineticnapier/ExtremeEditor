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
/// RED contract for ordered musical BPM sections. Timing points are not
/// ADOFAI SetSpeed events and do not claim playable floor timing.
/// </summary>
public sealed class MusicTimingMap
{
    private MusicTimingMap() { }

    public int SampleRate => throw new NotImplementedException();
    public IReadOnlyList<MusicTimingPoint> TimingPoints =>
        throw new NotImplementedException();

    public static MusicTimingMap Build(
        int sampleRate,
        IReadOnlyList<MusicTimingPoint> timingPoints) =>
        throw new NotImplementedException(
            "RED: build immutable musical timing sections on the audio-sample axis.");

    public MusicTimingSection FindSection(long audioSample) =>
        throw new NotImplementedException();

    public MusicTimingSnap Snap(
        long audioSample,
        MusicBeatSubdivision subdivision) =>
        throw new NotImplementedException();

    public IReadOnlyList<MusicTimingGridLine> GetVisibleLines(
        long startSample,
        long endSampleExclusive,
        MusicBeatSubdivision subdivision,
        int maximumLineCount = 10_000) =>
        throw new NotImplementedException();
}
