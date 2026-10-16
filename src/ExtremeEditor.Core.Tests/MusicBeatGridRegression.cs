using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class MusicBeatGridRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        MusicBeatGrid grid = MusicBeatGrid.CreateConstant(
            Rate, 120m, beatOriginSample: 24_000);

        Equal(Rate, grid.SampleRate, "sample rate");
        Equal(120m, grid.BeatsPerMinute, "BPM");
        Equal(24_000L, grid.BeatOriginSample, "beat origin");

        var expectedSteps = new Dictionary<MusicBeatSubdivision, long>
        {
            [MusicBeatSubdivision.One] = 24_000,
            [MusicBeatSubdivision.Half] = 12_000,
            [MusicBeatSubdivision.Third] = 8_000,
            [MusicBeatSubdivision.Quarter] = 6_000,
            [MusicBeatSubdivision.Eighth] = 3_000,
            [MusicBeatSubdivision.Sixteenth] = 1_500
        };
        foreach ((MusicBeatSubdivision subdivision, long step) in expectedSteps)
        {
            Equal(24_000L + step, grid.GetSample(1, subdivision),
                subdivision + " positive tick");
            Equal(24_000L - step, grid.GetSample(-1, subdivision),
                subdivision + " negative tick");
        }

        IReadOnlyList<MusicBeatGridLine> visible = grid.GetVisibleLines(
            20_000, 43_000, MusicBeatSubdivision.Quarter);
        Sequence([24_000L, 30_000L, 36_000L, 42_000L],
            visible.Select(line => line.AudioSample), "visible samples");
        Sequence([0L, 1L, 2L, 3L],
            visible.Select(line => line.TickIndex), "visible ticks");
        Sequence([true, false, false, false],
            visible.Select(line => line.IsWholeBeat), "whole-beat markers");
        if (visible is IList<MusicBeatGridLine> list)
            Check(list.IsReadOnly, "visible result must be immutable");

        MusicBeatSnap beforeMidpoint = grid.Snap(
            26_999, MusicBeatSubdivision.Quarter);
        Equal(24_000L, beforeMidpoint.AudioSample, "snap before midpoint");
        Equal(0L, beforeMidpoint.TickIndex, "snap tick before midpoint");
        MusicBeatSnap midpoint = grid.Snap(
            27_000, MusicBeatSubdivision.Quarter);
        Equal(30_000L, midpoint.AudioSample,
            "positive midpoint resolves away from origin");
        MusicBeatSnap negativeMidpoint = grid.Snap(
            21_000, MusicBeatSubdivision.Quarter);
        Equal(18_000L, negativeMidpoint.AudioSample,
            "negative midpoint resolves away from origin");

        const long distantTick = 10_000_000;
        MusicBeatGrid fractional = MusicBeatGrid.CreateConstant(
            Rate, 123.456m, beatOriginSample: 1_234);
        long expectedDistant = checked(1_234L + (long)decimal.Round(
            distantTick * (decimal)Rate * 60m / (123.456m * 16m),
            0, MidpointRounding.AwayFromZero));
        long distantSample = fractional.GetSample(
            distantTick, MusicBeatSubdivision.Sixteenth);
        Equal(expectedDistant, distantSample,
            "large tick is calculated directly without cumulative drift");
        Equal(distantTick, fractional.Snap(
            distantSample, MusicBeatSubdivision.Sixteenth).TickIndex,
            "large grid position roundtrips through snapping");

        IReadOnlyList<MusicBeatGridLine> exactEnd = grid.GetVisibleLines(
            24_000, 30_000, MusicBeatSubdivision.Quarter);
        Sequence([24_000L], exactEnd.Select(line => line.AudioSample),
            "visible end is exclusive");

        ExpectRejected(() => MusicBeatGrid.CreateConstant(0, 120m, 0),
            "zero sample rate");
        ExpectRejected(() => MusicBeatGrid.CreateConstant(Rate, 0m, 0),
            "zero BPM");
        ExpectRejected(() => grid.GetSample(0, (MusicBeatSubdivision)5),
            "unsupported subdivision");
        ExpectRejected(() => grid.GetVisibleLines(
                10, 10, MusicBeatSubdivision.One),
            "empty visible range");
        ExpectRejected(() => grid.GetVisibleLines(
                0, 100_000, MusicBeatSubdivision.Sixteenth, maximumLineCount: 2),
            "visible line budget exceeded");
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe music-grid input accepted: " + label);
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
