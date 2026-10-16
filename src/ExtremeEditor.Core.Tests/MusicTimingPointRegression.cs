using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class MusicTimingPointRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        var source = new List<MusicTimingPoint>
        {
            new(0, 120m),
            new(96_000, 60m)
        };
        MusicTimingMap map = MusicTimingMap.Build(Rate, source);

        Equal(Rate, map.SampleRate, "sample rate");
        Equal(2, map.TimingPoints.Count, "timing point count");
        source[0] = new MusicTimingPoint(0, 999m);
        Equal(120m, map.TimingPoints[0].BeatsPerMinute,
            "built map is isolated from caller collection changes");
        if (map.TimingPoints is IList<MusicTimingPoint> points)
            Check(points.IsReadOnly, "timing point snapshot is immutable");

        MusicTimingSection before = map.FindSection(95_999);
        Equal(0, before.TimingPointIndex, "section before BPM cut");
        Equal(96_000L, before.EndSampleExclusive!.Value,
            "first section ends at cut");
        MusicTimingSection atCut = map.FindSection(96_000);
        Equal(1, atCut.TimingPointIndex, "cut belongs to new timing section");
        Check(atCut.EndSampleExclusive is null,
            "last timing section has no finite map end");

        IReadOnlyList<MusicTimingGridLine> whole = map.GetVisibleLines(
            60_000, 160_000, MusicBeatSubdivision.One);
        Sequence([72_000L, 96_000L, 144_000L],
            whole.Select(line => line.AudioSample),
            "whole-beat lines split at BPM cut");
        Sequence([0, 1, 1],
            whole.Select(line => line.TimingPointIndex),
            "line timing-section identity");
        Sequence([true, true, true],
            whole.Select(line => line.IsWholeBeat),
            "timing-point cut is a whole beat");
        Equal(1, whole.Count(line => line.AudioSample == 96_000),
            "timing-point boundary is emitted once");

        IReadOnlyList<MusicTimingGridLine> beforeFirst = map.GetVisibleLines(
            -25_000, 1, MusicBeatSubdivision.One);
        Sequence([-24_000L, 0L],
            beforeFirst.Select(line => line.AudioSample),
            "first timing point extrapolates backward");

        MusicTimingSnap leftSnap = map.Snap(
            95_000, MusicBeatSubdivision.One);
        Equal(96_000L, leftSnap.AudioSample,
            "snap before cut may select new-section boundary");
        Equal(1, leftSnap.TimingPointIndex,
            "boundary snap reports new timing point");
        MusicTimingSnap rightSnap = map.Snap(
            97_000, MusicBeatSubdivision.One);
        Equal(96_000L, rightSnap.AudioSample, "snap after cut");

        IReadOnlyList<MusicTimingGridLine> quarter = map.GetVisibleLines(
            90_000, 109_000, MusicBeatSubdivision.Quarter);
        Sequence([90_000L, 96_000L, 108_000L],
            quarter.Select(line => line.AudioSample),
            "subdivision switches spacing at timing point");

        ExpectRejected(() => MusicTimingMap.Build(0,
                [new MusicTimingPoint(0, 120m)]),
            "invalid sample rate");
        ExpectRejected(() => MusicTimingMap.Build(Rate, []),
            "empty timing point list");
        ExpectRejected(() => MusicTimingMap.Build(Rate,
                [new MusicTimingPoint(10, 120m), new MusicTimingPoint(5, 60m)]),
            "unordered timing points");
        ExpectRejected(() => MusicTimingMap.Build(Rate,
                [new MusicTimingPoint(10, 120m), new MusicTimingPoint(10, 60m)]),
            "duplicate timing point sample");
        ExpectRejected(() => MusicTimingMap.Build(Rate,
                [new MusicTimingPoint(0, 0m)]),
            "invalid timing point BPM");
        ExpectRejected(() => map.GetVisibleLines(
                0, 160_000, MusicBeatSubdivision.Sixteenth, 2),
            "total visible line budget exceeded");
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(
            "Unsafe musical timing map input accepted: " + label);
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Unexpected sequence: " + label);
    }

    private static void Equal<T>(T expected, T actual, string label)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException(
                $"{label}: expected {expected}, actual {actual}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
