using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio.Tests;

internal static class RealtimeAudioDiagnosticsRegression
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifyRuntimeHitSoundBypass();
        VerifyAudioPlayerExposesRuntimeHitSoundBypass();
    }

    private static void VerifyRuntimeHitSoundBypass()
    {
        const int sampleRate = 1_000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        LevelDocument level = CreateLevel();
        var timing = new TimingMap(
        [
            Floor(0, 0.0),
            Floor(1, 0.0)
        ]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([1f, 1f, 1f, 1f], 0.0)
        };

        var hits = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips);
        var song = new ZeroSampleProvider(format);
        var graph = new UnifiedAudioSampleProvider(song, hits, totalFrames: 4);

        PropertyInfo enabledProperty = typeof(UnifiedAudioSampleProvider).GetProperty(
            "HitSoundsEnabled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "UnifiedAudioSampleProvider.HitSoundsEnabled does not exist yet.");

        if (enabledProperty.PropertyType != typeof(bool) || !enabledProperty.CanRead || !enabledProperty.CanWrite)
            throw new InvalidOperationException("HitSoundsEnabled must be a readable/writable bool.");

        enabledProperty.SetValue(graph, false);
        var buffer = new float[4];
        int read = graph.Read(buffer, 0, buffer.Length);

        if (read != buffer.Length)
            throw new InvalidOperationException($"Expected {buffer.Length} samples, actual {read}.");
        if (buffer.Any(sample => Math.Abs(sample) > 0.000001f))
            throw new InvalidOperationException("Disabling hitsounds must bypass hit mixing completely.");
    }

    private static void VerifyAudioPlayerExposesRuntimeHitSoundBypass()
    {
        PropertyInfo enabledProperty = typeof(AudioPlayer).GetProperty(
            "HitSoundsEnabled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "AudioPlayer.HitSoundsEnabled does not exist yet.");

        if (enabledProperty.PropertyType != typeof(bool) || !enabledProperty.CanRead || !enabledProperty.CanWrite)
            throw new InvalidOperationException("AudioPlayer.HitSoundsEnabled must be a readable/writable bool.");

        using var player = new AudioPlayer();
        if (enabledProperty.GetValue(player) is not true)
            throw new InvalidOperationException("AudioPlayer hitsounds must be enabled by default.");

        enabledProperty.SetValue(player, false);
        if (enabledProperty.GetValue(player) is not false)
            throw new InvalidOperationException("AudioPlayer must retain the runtime hitsound bypass setting.");
    }

    private static FloorTiming Floor(int floor, double entryTime) =>
        new(floor, entryTime, entryTime + 1.0, 0.0, Math.PI, Math.PI, 120.0, false, false);

    private static LevelDocument CreateLevel()
    {
        return new LevelDocument
        {
            SourcePath = "<audio-diagnostics-test>",
            Angles = [0.0],
            Positions = [System.Numerics.Vector2.Zero, System.Numerics.Vector2.UnitX],
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
            InitialBpm = 120.0,
            SongFilename = null,
            OffsetMilliseconds = 0,
            PitchPercent = 100,
            CountdownTicks = 0,
            SeparateCountdownTime = false,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = new WorldRect(0, 0, 1, 0)
        };
    }

    private sealed class ZeroSampleProvider(WaveFormat waveFormat) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
