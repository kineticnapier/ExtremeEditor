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
        VerifyProviderPrecomputesHitSchedule();
        VerifyProviderCachesHitSoundPcmOnDemand();
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

        var hits = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames: 4);
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

    private static void VerifyProviderPrecomputesHitSchedule()
    {
        const int sampleRate = 1_000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        LevelDocument level = CreateLevel();
        var timing = new TimingMap(
        [
            Floor(0, 0.0),
            Floor(1, 0.25)
        ]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([1f, 1f, 1f, 1f], 0.0)
        };

        var provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames: 1_000);
        Type providerType = typeof(SampleAccurateHitSoundProvider);
        FieldInfo scheduleField = providerType.GetField(
            "_scheduledHits",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "SampleAccurateHitSoundProvider._scheduledHits does not exist yet.");

        object schedule = scheduleField.GetValue(provider)
            ?? throw new InvalidOperationException("Precomputed hitsound schedule must be initialized in the constructor.");
        PropertyInfo? countProperty = schedule.GetType().GetProperty("Count") ??
                                      schedule.GetType().GetProperty("Length");
        int count = countProperty?.GetValue(schedule) is int value
            ? value
            : throw new InvalidOperationException("Precomputed hitsound schedule must expose Count or Length.");
        if (count != 1)
            throw new InvalidOperationException($"Expected one scheduled audible hit, actual {count}.");

        foreach (string sourceField in new[] { "_level", "_timingMap", "_timeline", "_clips" })
        {
            if (providerType.GetField(sourceField, BindingFlags.Instance | BindingFlags.NonPublic) is not null)
            {
                throw new InvalidOperationException(
                    $"Realtime hitsound provider must not retain {sourceField}; floor/timing/timeline/lookup work belongs in constructor precomputation.");
            }
        }
    }

    private static void VerifyProviderCachesHitSoundPcmOnDemand()
    {
        const int sampleRate = 1_000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        LevelDocument level = CreateLevel();
        var timing = new TimingMap(
        [
            Floor(0, 0.0),
            Floor(1, 0.25)
        ]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([1f, 1f, 0.5f, 0.5f], 0.0)
        };

        var provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames: 1_000);
        Type providerType = typeof(SampleAccurateHitSoundProvider);

        FieldInfo renderedChunksField = providerType.GetField(
            "_renderedChunks",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "SampleAccurateHitSoundProvider._renderedChunks does not exist yet.");

        object cache = renderedChunksField.GetValue(provider)
            ?? throw new InvalidOperationException("Progressive hitsound PCM cache must be initialized in the constructor.");
        PropertyInfo countProperty = cache.GetType().GetProperty("Count")
            ?? throw new InvalidOperationException("Progressive hitsound PCM cache must expose Count.");

        int before = (int)(countProperty.GetValue(cache) ?? -1);
        if (before != 0)
            throw new InvalidOperationException($"Provider construction must not eagerly render PCM chunks; actual cache count {before}.");

        provider.Seek(250);
        var buffer = new float[4];
        provider.Read(buffer, 0, buffer.Length);

        int after = (int)(countProperty.GetValue(cache) ?? -1);
        if (after <= 0)
            throw new InvalidOperationException("Reading an uncached range must render and retain the required PCM chunk.");
        if (Math.Abs(buffer[0] - 1f) > 0.0001f || Math.Abs(buffer[2] - 0.5f) > 0.0001f)
            throw new InvalidOperationException(
                $"On-demand cached PCM changed sample-accurate output: actual {buffer[0]}, {buffer[2]}.");

        if (providerType.GetField("_activeVoices", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
        {
            throw new InvalidOperationException(
                "Realtime provider must not retain an active-voice mixer; cache misses render independent PCM chunks.");
        }
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
