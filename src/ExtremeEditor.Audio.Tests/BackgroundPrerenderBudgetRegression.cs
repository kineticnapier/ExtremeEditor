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

        VerifyCancelledCachedChunkDoesNotTouchLru(provider, providerType, renderedChunks);
        VerifyCancelledMixDoesNotWriteAndNormalMixIsUnchanged(providerType);
    }

    private static void VerifyCancelledCachedChunkDoesNotTouchLru(
        SampleAccurateHitSoundProvider provider, Type providerType, IDictionary renderedChunks)
    {
        // The cached-chunk fast path must observe cancellation before touching LRU.
        // This is deterministic; cancellation DURING mixing needs a separate test.
        FieldInfo lruField = providerType.GetField(
            "_cacheLru", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM cache LRU field is unavailable.");
        if (lruField.GetValue(provider) is not LinkedList<long> lru || lru.Count == 0)
            throw new InvalidOperationException("Expected a populated PCM cache LRU.");
        long[] before = lru.ToArray();
        long cachedChunk = before[0];
        int countBefore = renderedChunks.Count;
        long bytesBefore = provider.CachedPcmBytes;
        MethodInfo ensureMethod = providerType.GetMethod(
            "TryEnsureChunkWithinBudget", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("Background cache insertion method is unavailable.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = ensureMethod.Invoke(provider, [cachedChunk, long.MaxValue, cancellation.Token]);
            throw new InvalidOperationException("Cancelled cached-chunk lookup unexpectedly succeeded.");
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is OperationCanceledException)
        {
            // Expected: no cache hit is allowed to update LRU after cancellation.
        }
        if (!before.SequenceEqual(lru) ||
            countBefore != renderedChunks.Count ||
            bytesBefore != provider.CachedPcmBytes)
            throw new InvalidOperationException(
                "Cancelled background cache lookup modified the cache or LRU.");
    }

    private static void VerifyCancelledMixDoesNotWriteAndNormalMixIsUnchanged(Type providerType)
    {
        // Exercise the exact mixing routine reached by background prerendering.
        // This checks the cancellation boundary and unchanged arithmetic; it does
        // not claim to reproduce cancellation while a vector loop is running.
        MethodInfo addScaled = providerType.GetMethod(
            "AddScaled", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(float[]), typeof(int), typeof(float[]), typeof(int),
                    typeof(int), typeof(float), typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException("PCM mixer cancellation overload is missing.");

        const int samples = 8192;
        float[] source = Enumerable.Range(0, samples)
            .Select(i => (float)((i % 17) - 8) / 32f).ToArray();
        float[] destination = Enumerable.Repeat(0.125f, samples).ToArray();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            addScaled.Invoke(null, [destination, 0, source, 0, samples, 0.5f, cancelled.Token]);
            throw new InvalidOperationException("Cancelled PCM mix unexpectedly completed.");
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is OperationCanceledException)
        {
            // Cancellation must occur before any destination sample is modified.
        }
        if (destination.Any(value => value != 0.125f))
            throw new InvalidOperationException("Cancelled PCM mix modified its destination.");

        addScaled.Invoke(null, [destination, 0, source, 0, samples, 0.5f, CancellationToken.None]);
        for (int i = 0; i < samples; i++)
        {
            float expected = 0.125f + source[i] * 0.5f;
            if (destination[i] != expected)
                throw new InvalidOperationException(
                    $"PCM mix differs at sample {i}: expected={expected}, actual={destination[i]}.");
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
