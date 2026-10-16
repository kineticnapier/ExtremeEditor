using System.Numerics;

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
/// An osu!-style constant-BPM grid on the decoded-audio
/// sample axis. Grid positions are independent from ADOFAI floor timing.
/// </summary>
public sealed class MusicBeatGrid
{
    private readonly BigInteger _samplesPerBeatNumerator;
    private readonly BigInteger _samplesPerBeatDenominator;

    private MusicBeatGrid(
        int sampleRate,
        decimal beatsPerMinute,
        long beatOriginSample,
        BigInteger samplesPerBeatNumerator,
        BigInteger samplesPerBeatDenominator)
    {
        SampleRate = sampleRate;
        BeatsPerMinute = beatsPerMinute;
        BeatOriginSample = beatOriginSample;
        _samplesPerBeatNumerator = samplesPerBeatNumerator;
        _samplesPerBeatDenominator = samplesPerBeatDenominator;
    }

    public int SampleRate { get; }
    public decimal BeatsPerMinute { get; }
    public long BeatOriginSample { get; }

    public static MusicBeatGrid CreateConstant(
        int sampleRate,
        decimal beatsPerMinute,
        long beatOriginSample)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate),
                "The decoded-audio sample rate must be positive.");
        if (beatsPerMinute <= 0m)
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute),
                "Music-grid BPM must be positive.");

        (BigInteger bpmNumerator, BigInteger bpmDenominator) =
            DecimalFraction(beatsPerMinute);
        BigInteger numerator = (BigInteger)sampleRate * 60 * bpmDenominator;
        BigInteger denominator = bpmNumerator;
        BigInteger divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new MusicBeatGrid(
            sampleRate,
            beatsPerMinute,
            beatOriginSample,
            numerator / divisor,
            denominator / divisor);
    }

    public long GetSample(
        long tickIndex,
        MusicBeatSubdivision subdivision)
    {
        int divisor = RequireSubdivision(subdivision);
        EnsureSampleResolution(divisor);
        BigInteger delta = DivideRoundedAwayFromZero(
            (BigInteger)tickIndex * _samplesPerBeatNumerator,
            _samplesPerBeatDenominator * divisor);
        return CheckedInt64((BigInteger)BeatOriginSample + delta,
            "The requested music-grid sample is outside Int64 range.");
    }

    public MusicBeatSnap Snap(
        long audioSample,
        MusicBeatSubdivision subdivision)
    {
        int divisor = RequireSubdivision(subdivision);
        EnsureSampleResolution(divisor);
        BigInteger sampleDelta = (BigInteger)audioSample - BeatOriginSample;
        BigInteger tick = DivideRoundedAwayFromZero(
            sampleDelta * _samplesPerBeatDenominator * divisor,
            _samplesPerBeatNumerator);
        long tickIndex = CheckedInt64(tick,
            "The snapped music-grid tick is outside Int64 range.");
        return new MusicBeatSnap(GetSample(tickIndex, subdivision), tickIndex);
    }

    public IReadOnlyList<MusicBeatGridLine> GetVisibleLines(
        long startSample,
        long endSampleExclusive,
        MusicBeatSubdivision subdivision,
        int maximumLineCount = 10_000)
    {
        if (endSampleExclusive <= startSample)
            throw new ArgumentException(
                "The visible music-grid range must be nonempty.",
                nameof(endSampleExclusive));
        if (maximumLineCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLineCount),
                "The visible music-grid line budget must be positive.");

        int divisor = RequireSubdivision(subdivision);
        EnsureSampleResolution(divisor);
        BigInteger relativeStart = (BigInteger)startSample - BeatOriginSample;
        BigInteger firstEstimate = FloorDivide(
            relativeStart * _samplesPerBeatDenominator * divisor,
            _samplesPerBeatNumerator) - 2;
        long tick = CheckedInt64(firstEstimate,
            "The first visible music-grid tick is outside Int64 range.");

        while (GetSample(tick, subdivision) < startSample)
        {
            if (tick == long.MaxValue)
                throw new OverflowException("The visible music-grid tick overflowed.");
            tick++;
        }
        while (tick > long.MinValue &&
               GetSample(tick - 1, subdivision) >= startSample)
        {
            tick--;
        }

        var result = new List<MusicBeatGridLine>();
        while (true)
        {
            long sample = GetSample(tick, subdivision);
            if (sample >= endSampleExclusive)
                break;
            if (result.Count == maximumLineCount)
                throw new InvalidOperationException(
                    "The visible music-grid line budget was exceeded. Zoom in or raise the explicit limit.");

            result.Add(new MusicBeatGridLine(
                sample,
                tick,
                tick % divisor == 0));
            if (tick == long.MaxValue)
                throw new OverflowException("The visible music-grid tick overflowed.");
            tick++;
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private void EnsureSampleResolution(int subdivision)
    {
        if (_samplesPerBeatNumerator <
            _samplesPerBeatDenominator * subdivision)
        {
            throw new InvalidOperationException(
                "The requested subdivision is finer than one decoded-audio sample.");
        }
    }

    private static int RequireSubdivision(MusicBeatSubdivision subdivision) =>
        subdivision switch
        {
            MusicBeatSubdivision.One => 1,
            MusicBeatSubdivision.Half => 2,
            MusicBeatSubdivision.Third => 3,
            MusicBeatSubdivision.Quarter => 4,
            MusicBeatSubdivision.Eighth => 8,
            MusicBeatSubdivision.Sixteenth => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(subdivision),
                "Supported beat subdivisions are 1, 2, 3, 4, 8 and 16.")
        };

    private static (BigInteger Numerator, BigInteger Denominator)
        DecimalFraction(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger numerator = ((BigInteger)(uint)bits[2] << 64) |
                               ((BigInteger)(uint)bits[1] << 32) |
                               (uint)bits[0];
        int scale = (bits[3] >> 16) & 0x7F;
        BigInteger denominator = BigInteger.Pow(10, scale);
        BigInteger divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return (numerator / divisor, denominator / divisor);
    }

    private static BigInteger DivideRoundedAwayFromZero(
        BigInteger numerator,
        BigInteger denominator)
    {
        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator));
        if (numerator.IsZero)
            return BigInteger.Zero;

        int sign = numerator.Sign;
        BigInteger absolute = BigInteger.Abs(numerator);
        BigInteger quotient = BigInteger.DivRem(
            absolute, denominator, out BigInteger remainder);
        if (remainder * 2 >= denominator)
            quotient++;
        return sign > 0 ? quotient : -quotient;
    }

    private static BigInteger FloorDivide(
        BigInteger numerator,
        BigInteger denominator)
    {
        BigInteger quotient = BigInteger.DivRem(
            numerator, denominator, out BigInteger remainder);
        return numerator.Sign < 0 && !remainder.IsZero
            ? quotient - 1
            : quotient;
    }

    private static long CheckedInt64(BigInteger value, string message)
    {
        if (value < long.MinValue || value > long.MaxValue)
            throw new OverflowException(message);
        return (long)value;
    }
}
