using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio.Tests;

internal static class BackgroundPrerenderBudgetRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        const int sampleRate = 1_000;
        const int chunkFrames = 4096;
        const int hitCount = 24;
        const double hitSpacingSeconds = 5.0;
        const long cacheBudgetBytes = 2L * chunkFrames * 2 * sizeof(float);

        var timings = new FloorTiming[hitCount + 1];
        timings[0] = Floor(0, 0.0);
        for (int floor = 1; floor < timings.Length; floor++)
            timings[floor] = Floor(floor, floor * hitSpacingSeconds);

        LevelDocument level = CreateLevel(timings.Length);
        var timingMap = new TimingMap(timings);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.25f, 0.25f], 0.0)
        };
        long totalFrames = checked((long)((hitCount + 2) * hitSpacingSeconds * sampleRate));

        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level,
            timingMap,
            timeline,
            clips,
            totalFrames);

        if (provider.Metrics.RenderedChunkCount != 0)
            throw new InvalidOperationException("background prerender fixture must start with an empty PCM cache");

        Type providerType = typeof(SampleAccurateHitSoundProvider);
        MethodInfo prerenderMethod = providerType.GetMethod(
            "PrerenderAheadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "RED: SampleAccurateHitSoundProvider.PrerenderAheadAsync(startFrame, cacheBudgetBytes, cancellationToken) does not exist.");

        PropertyInfo cachedBytesProperty = providerType.GetProperty(
            "CachedPcmBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: SampleAccurateHitSoundProvider.CachedPcmBytes does not exist.");

        object? taskObject = prerenderMethod.Invoke(
            provider,
            [0L, cacheBudgetBytes, CancellationToken.None]);
        if (taskObject is not Task task)
            throw new InvalidOperationException("PrerenderAheadAsync must return Task.");
        if (!task.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("background prerender did not finish within 5 seconds");

        long cachedBytes = cachedBytesProperty.GetValue(provider) is long bytes
            ? bytes
            : throw new InvalidOperationException("CachedPcmBytes must be a long.");
        if (cachedBytes > cacheBudgetBytes)
        {
            throw new InvalidOperationException(
                $"RED: background prerender exceeded its PCM budget: cached={cachedBytes} budget={cacheBudgetBytes}.");
        }

        FieldInfo renderedChunksField = providerType.GetField(
            "_renderedChunks",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SampleAccurateHitSoundProvider._renderedChunks is unavailable.");
        if (renderedChunksField.GetValue(provider) is not IDictionary renderedChunks)
            throw new InvalidOperationException("Rendered chunk cache must expose IDictionary for regression inspection.");

        bool renderedFutureChunk = false;
        foreach (object key in renderedChunks.Keys)
        {
            if (Convert.ToInt64(key) > 0)
            {
                renderedFutureChunk = true;
                break;
            }
        }

        if (!renderedFutureChunk)
        {
            throw new InvalidOperationException(
                "RED: background prerender must populate at least one future chunk that was never requested by Read().");
        }
    }

    private static FloorTiming Floor(int floor, double entry) =>
        new(floor, entry, entry + 1.0 / 48_000, 0, 0, Math.PI, 100, false, false);

    private static LevelDocument CreateLevel(int floors) => new()
    {
        SourcePath = "<background-prerender-test>",
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
}
