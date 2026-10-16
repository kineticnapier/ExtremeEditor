namespace ExtremeEditor.Core;

public enum MusicBeatSubdivision : byte
{
    One = 1,
    Half = 2,
    Third = 3,
    Quarter = 4,
    Eighth = 8,
    Sixteenth = 16
}

public sealed record MusicBeatGridLine(
    long AudioSample,
    long TickIndex,
    bool IsWholeBeat);

public sealed record MusicBeatSnap(
    long AudioSample,
    long TickIndex);

/// <summary>
/// RED contract for an osu!-style constant-BPM grid on the decoded-audio
/// sample axis. Grid positions are independent from ADOFAI floor timing.
/// </summary>
public sealed class MusicBeatGrid
{
    private MusicBeatGrid() { }

    public int SampleRate => throw new NotImplementedException();
    public decimal BeatsPerMinute => throw new NotImplementedException();
    public long BeatOriginSample => throw new NotImplementedException();

    public static MusicBeatGrid CreateConstant(
        int sampleRate,
        decimal beatsPerMinute,
        long beatOriginSample) =>
        throw new NotImplementedException(
            "RED: build a constant BPM grid on the shared audio-sample axis.");

    public long GetSample(
        long tickIndex,
        MusicBeatSubdivision subdivision) =>
        throw new NotImplementedException();

    public MusicBeatSnap Snap(
        long audioSample,
        MusicBeatSubdivision subdivision) =>
        throw new NotImplementedException();

    public IReadOnlyList<MusicBeatGridLine> GetVisibleLines(
        long startSample,
        long endSampleExclusive,
        MusicBeatSubdivision subdivision,
        int maximumLineCount = 10_000) =>
        throw new NotImplementedException();
}
