using System.Numerics;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class DenseMixSafetyRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        RequireLinearHitSoundBus();
        RequireLimitedUnifiedMix();
    }

    private static void RequireLinearHitSoundBus()
    {
        const int sampleRate = 1_000;
        LevelDocument level = CreateLevel(3);
        var timing = new TimingMap([
            Floor(0, 0.0),
            Floor(1, 1.0),
            Floor(2, 1.0)
        ]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.75f, 0.75f], 0.0)
        };
        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level,
            timing,
            timeline,
            clips,
            totalFrames: 2_000);

        provider.Seek(sampleRate);
        var buffer = new float[2];
        provider.Read(buffer, 0, buffer.Length);

        foreach (float sample in buffer)
        {
            if (!float.IsFinite(sample))
                throw new InvalidOperationException($"dense hitsound bus: expected finite PCM, actual {sample}");
            if (Math.Abs(sample - 1.5f) > 0.0001f)
                throw new InvalidOperationException(
                    $"dense hitsound bus: expected unclipped linear sum 1.5, actual {sample}");
        }
    }

    private static void RequireLimitedUnifiedMix()
    {
        const int sampleRate = 1_000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        LevelDocument level = CreateLevel(2);
        var timing = new TimingMap([
            Floor(0, 0.0),
            Floor(1, 0.0)
        ]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([
                0.75f, 0.75f,
                0.375f, 0.375f
            ], 0.0)
        };
        var hitSounds = new SampleAccurateHitSoundProvider(
            format,
            level,
            timing,
            timeline,
            clips,
            totalFrames: 2);
        var song = new ConstantSampleProvider(format, 0.75f);
        var provider = new UnifiedAudioSampleProvider(song, hitSounds, totalFrames: 2);
        var buffer = new float[4];

        provider.Read(buffer, 0, buffer.Length);

        RequireSafePcm(buffer, "unified song and hitsound mix");

        if (Math.Abs(buffer[0] - buffer[1]) > 0.0001f || Math.Abs(buffer[2] - buffer[3]) > 0.0001f)
            throw new InvalidOperationException("master limiter must preserve linked stereo gain");

        if (buffer[0] < 0.99f || buffer[0] > 1.0f)
            throw new InvalidOperationException($"master limiter: expected first peak near ceiling, actual {buffer[0]}");

        if (buffer[2] >= buffer[0] - 0.05f)
            throw new InvalidOperationException(
                $"master limiter: expected waveform dynamics instead of flat-top clipping, actual {buffer[0]}, {buffer[2]}");
    }

    private static void RequireSafePcm(IEnumerable<float> samples, string name)
    {
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
                throw new InvalidOperationException($"{name}: expected finite PCM, actual {sample}");
            if (Math.Abs(sample) > 1.0f)
                throw new InvalidOperationException($"{name}: expected abs(sample) <= 1, actual {sample}");
        }
    }

    private static FloorTiming Floor(int floor, double entry) =>
        new(floor, entry, entry + 1.0 / 1_000, 0, 0, Math.PI, 100, false, false);

    private static LevelDocument CreateLevel(int floors) => new()
    {
        SourcePath = "<test>",
        Angles = new double[Math.Max(0, floors - 1)],
        Positions = new Vector2[floors],
        ActionCount = 0,
        ActionTypeCounts = new Dictionary<string, int>(),
        ActionsByFloor = new Dictionary<int, LevelAction[]>(),
        InitialBpm = 100,
        SongFilename = null,
        OffsetMilliseconds = 0,
        PitchPercent = 100,
        CountdownTicks = 0,
        SeparateCountdownTime = false,
        DefaultHitSound = "Kick",
        HitSoundVolumePercent = 100,
        Bounds = default
    };

    private sealed class ConstantSampleProvider(WaveFormat waveFormat, float sample) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, sample, offset, count);
            return count;
        }
    }
}
