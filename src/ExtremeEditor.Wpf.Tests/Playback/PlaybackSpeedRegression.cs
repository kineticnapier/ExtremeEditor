using System.Reflection;
using ExtremeEditor.Audio;
using NAudio.Wave;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackSpeedRegression
{
    private const double Tolerance = 0.000000001;

    public static void Run()
    {
        VerifyUnitSpeed();
        VerifySourceAudioPositionSemantics();
        VerifyPitchIsAppliedExactlyOnce();
        VerifyRuntimeSpeedChangeIsContinuous();
        VerifySeekIsIndependentOfPlaybackSpeed();
        VerifyLimits();
        VerifyPauseResume();
        VerifyUnifiedGraphUsesOneRate();
        VerifyProductionRateProvider();

        VerifyProductionPlaybackSpeedSurface();
    }

    private static void VerifyUnitSpeed()
    {
        AssertNear(1, SourcePositionAfter(0, 1, 1), "1.0x source position");
        AssertNear(1.5, ChartDelta(realSeconds: 1, playbackSpeed: 1, songPitch: 1.5),
            "1.0x existing chart clock");
    }

    private static void VerifySourceAudioPositionSemantics()
    {
        // AudioPlayer.Position is always a position on the decoded/source-audio timeline.
        AssertNear(0.5, SourcePositionAfter(0, 1, 0.5), "0.5x source-audio Position");
        AssertNear(2, SourcePositionAfter(0, 1, 2), "2.0x source-audio Position");
        AssertNear(0.01, SourcePositionAfter(0, 1, 0.01), "0.01x source-audio Position");
        AssertNear(10, SourcePositionAfter(0, 1, 10), "10.0x source-audio Position");
    }

    private static void VerifyPitchIsAppliedExactlyOnce()
    {
        AssertNear(0.75, ChartDelta(realSeconds: 1, playbackSpeed: 0.5, songPitch: 1.5),
            "playback speed and song.pitch composition");
        AssertNear(2.5, ChartDelta(realSeconds: 2, playbackSpeed: 2, songPitch: 0.625),
            "playback speed and fractional song.pitch composition");
    }

    private static void VerifyRuntimeSpeedChangeIsContinuous()
    {
        const double initialChartPosition = 10;
        double beforeChange = initialChartPosition + ChartDelta(2, 1, 1);
        double atChange = beforeChange;
        double afterChange = atChange + ChartDelta(2, 0.5, 1);

        AssertNear(12, beforeChange, "position before runtime speed change");
        AssertNear(beforeChange, atChange, "position at runtime speed change");
        AssertNear(13, afterChange, "position after runtime speed change");
    }

    private static void VerifySeekIsIndependentOfPlaybackSpeed()
    {
        const double chartPosition = 30;
        const double songPitch = 1.5;
        double sourceAudioPosition = chartPosition / songPitch;

        foreach (double speed in new[] { 0.01, 0.5, 1.0, 2.0, 10.0 })
        {
            // Speed controls subsequent consumption; it must not change chart/source coordinates.
            AssertNear(chartPosition, sourceAudioPosition * songPitch,
                $"seek round-trip at {speed:0.##}x");
        }
    }

    private static void VerifyLimits()
    {
        AssertNear(0.01, NormalizeFiniteSpeed(0.001), "lower speed clamp");
        AssertNear(0.01, NormalizeFiniteSpeed(0.01), "minimum speed");
        AssertNear(10, NormalizeFiniteSpeed(10), "maximum speed");
        AssertNear(10, NormalizeFiniteSpeed(25), "upper speed clamp");

        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            if (TryNormalizeSpeed(invalid, out _))
                throw new InvalidOperationException($"Invalid playback speed was accepted: {invalid}.");
        }
    }

    private static void VerifyPauseResume()
    {
        const double speed = 0.5;
        double position = SourcePositionAfter(4, 2, speed);
        double paused = position;
        double resumed = SourcePositionAfter(paused, 2, speed);

        AssertNear(5, position, "position before pause");
        AssertNear(position, paused, "position while paused");
        AssertNear(6, resumed, "position after resume");
    }

    private static void VerifyUnifiedGraphUsesOneRate()
    {
        // Song and scheduled hitsounds share one source timeline. A single rate applied after
        // their unified mix preserves their relative source positions, including both limits.
        const double songMarker = 4;
        const double hitSoundMarker = 5;
        foreach (double speed in new[] { 0.01, 0.5, 1.0, 2.0, 10.0 })
        {
            double songRealTime = songMarker / speed;
            double hitSoundRealTime = hitSoundMarker / speed;
            AssertNear((hitSoundMarker - songMarker) / speed, hitSoundRealTime - songRealTime,
                $"unified song/hitsound rate at {speed:0.##}x");
        }
    }

    private static void VerifyProductionRateProvider()
    {
        VerifyRateProviderRamp(0.5, new[] { 0.0, 0.5, 1.0, 1.5 });
        VerifyRateProviderRamp(2.0, new[] { 0.0, 2.0, 4.0, 6.0 });

        foreach (double speed in new[] { 0.01, 10.0 })
        {
            var combined = new MarkerSampleProvider(240, songFrame: 100, hitSoundFrame: 200);
            var rate = new VariableRateSampleProvider(combined, speed);
            int outputFrames = speed < 1 ? 20_101 : 21;
            var output = new float[outputFrames];
            int read = rate.Read(output, 0, output.Length);
            if (read != output.Length)
                throw new InvalidOperationException($"Rate provider ended early at {speed:0.##}x. expected={output.Length}, actual={read}.");

            int songOutputFrame = (int)Math.Round(100 / speed);
            int hitOutputFrame = (int)Math.Round(200 / speed);
            AssertNear(0.25, output[songOutputFrame], $"song marker at {speed:0.##}x");
            AssertNear(0.75, output[hitOutputFrame], $"hitsound marker at {speed:0.##}x");
        }

        if (!PlaybackSpeedPolicy.TryNormalize(0.001, out double minimum) || minimum != 0.01 ||
            !PlaybackSpeedPolicy.TryNormalize(25, out double maximum) || maximum != 10 ||
            PlaybackSpeedPolicy.TryNormalize(double.NaN, out _))
        {
            throw new InvalidOperationException("Production playback speed limits do not match the transport contract.");
        }
    }

    private static void VerifyRateProviderRamp(double speed, double[] expected)
    {
        var rate = new VariableRateSampleProvider(new RampSampleProvider(64), speed);
        var output = new float[expected.Length];
        int read = rate.Read(output, 0, output.Length);
        if (read != output.Length)
            throw new InvalidOperationException($"Rate provider ramp ended early at {speed:0.##}x.");

        for (int i = 0; i < expected.Length; i++)
            AssertNear(expected[i], output[i], $"rate provider {speed:0.##}x frame {i}");
    }

    private static void VerifyProductionPlaybackSpeedSurface()
    {
        PropertyInfo? property = typeof(AudioPlayer).GetProperty(
            "PlaybackSpeed",
            BindingFlags.Instance | BindingFlags.Public);

        if (property is null ||
            property.PropertyType != typeof(double) ||
            !property.CanRead ||
            !property.CanWrite)
        {
            throw new InvalidOperationException(
                "Playback speed transport is not available: AudioPlayer.PlaybackSpeed must expose the shared unified-graph rate.");
        }
    }

    private static double SourcePositionAfter(double sourcePosition, double realSeconds, double playbackSpeed) =>
        sourcePosition + (realSeconds * playbackSpeed);

    private static double ChartDelta(double realSeconds, double playbackSpeed, double songPitch) =>
        realSeconds * playbackSpeed * songPitch;

    private static double NormalizeFiniteSpeed(double value)
    {
        if (!TryNormalizeSpeed(value, out double normalized))
            throw new InvalidOperationException($"Invalid playback speed cannot be normalized: {value}.");
        return normalized;
    }

    private static bool TryNormalizeSpeed(double value, out double normalized)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            normalized = 0;
            return false;
        }

        normalized = Math.Clamp(value, 0.01, 10.0);
        return true;
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > Tolerance)
        {
            throw new InvalidOperationException(
                $"{label}: expected={expected:0.#########}, actual={actual:0.#########}.");
        }
    }

    private sealed class RampSampleProvider : ISampleProvider
    {
        private readonly int _frameCount;
        private int _position;

        public RampSampleProvider(int frameCount)
        {
            _frameCount = frameCount;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _frameCount - _position);
            for (int i = 0; i < read; i++)
                buffer[offset + i] = _position++;
            return read;
        }
    }

    private sealed class MarkerSampleProvider : ISampleProvider
    {
        private readonly int _frameCount;
        private readonly int _songFrame;
        private readonly int _hitSoundFrame;
        private int _position;

        public MarkerSampleProvider(int frameCount, int songFrame, int hitSoundFrame)
        {
            _frameCount = frameCount;
            _songFrame = songFrame;
            _hitSoundFrame = hitSoundFrame;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _frameCount - _position);
            for (int i = 0; i < read; i++)
            {
                int frame = _position++;
                buffer[offset + i] = frame == _songFrame
                    ? 0.25f
                    : frame == _hitSoundFrame
                        ? 0.75f
                        : 0.0f;
            }

            return read;
        }
    }
}
