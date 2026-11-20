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

        // Use a fresh provider so the target chunk must actually render before
        // the cancellation checkpoint, rather than taking the cached fast path.
        var uncachedProvider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level, timingMap, timeline, clips, totalFrames);
        VerifyCancellationAfterRenderDoesNotPublishChunk(uncachedProvider, providerType);
        VerifyAsyncPrerenderCancellationBeforeCache(
            level, timingMap, timeline, clips, totalFrames, providerType, sampleRate, cacheBudgetBytes);
        VerifyExternalThreadCancellationBeforeCache(
            level, timingMap, timeline, clips, totalFrames, providerType, sampleRate, cacheBudgetBytes);
        VerifyExternalCancellationWithCacheLockContended(
            level, timingMap, timeline, clips, totalFrames, providerType, sampleRate, cacheBudgetBytes);
    }

    private static void VerifyAsyncPrerenderCancellationBeforeCache(
        LevelDocument level, TimingMap timingMap, HitSoundTimeline timeline,
        Dictionary<string, RenderedHitSound> clips, long totalFrames,
        Type providerType, int sampleRate, long cacheBudgetBytes)
    {
        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level, timingMap, timeline, clips, totalFrames);
        MethodInfo method = providerType.GetMethod(
            "PrerenderAheadAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken), typeof(Action)],
            modifiers: null)
            ?? throw new InvalidOperationException("Async prerender cancellation hook is missing.");
        FieldInfo cacheField = providerType.GetField(
            "_renderedChunks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM cache field is missing.");
        FieldInfo lruField = providerType.GetField(
            "_cacheLru", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM LRU field is missing.");
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int workerThread = 0;
        Action cancelAfterRender = () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref checkpoints);
            cancellation.Cancel();
        };
        // Start at the first audible hit (frame 5000), not at a silent chunk.
        if (method.Invoke(provider, [5000L, cacheBudgetBytes, cancellation.Token, cancelAfterRender]) is not Task task)
            throw new InvalidOperationException("Async prerender did not return a Task.");
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Async prerender cancellation timed out.");
            throw new InvalidOperationException("Cancelled async prerender unexpectedly succeeded.");
        }
        catch (AggregateException exception)
            when (exception.InnerExceptions.Count == 1 &&
                  exception.InnerException is OperationCanceledException)
        {
            // Expected: the actual Task.Run path propagates cancellation.
        }
        if (checkpoints != 1 || workerThread == 0 || workerThread == callerThread ||
            provider.RenderChunkAttempts != 1 ||
            provider.CachedPcmBytes != 0 || provider.Metrics.RenderedChunkCount != 0 ||
            cacheField.GetValue(provider) is not IDictionary chunks || chunks.Count != 0 ||
            lruField.GetValue(provider) is not LinkedList<long> lru || lru.Count != 0)
            throw new InvalidOperationException("Async cancellation published PCM or did not reach worker checkpoint.");

        // The same provider must still render and cache normally afterwards.
        provider.PrerenderAheadAsync(5000L, cacheBudgetBytes, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (chunks.Count == 0 || lru.Count == 0 || provider.CachedPcmBytes == 0)
            throw new InvalidOperationException("Async prerender did not recover after cancellation.");
    }

    private static void VerifyExternalThreadCancellationBeforeCache(
        LevelDocument level, TimingMap timingMap, HitSoundTimeline timeline,
        Dictionary<string, RenderedHitSound> clips, long totalFrames,
        Type providerType, int sampleRate, long cacheBudgetBytes)
    {
        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level, timingMap, timeline, clips, totalFrames);
        MethodInfo method = providerType.GetMethod(
            "PrerenderAheadAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken), typeof(Action)],
            modifiers: null)
            ?? throw new InvalidOperationException("Async prerender cancellation hook is missing.");
        FieldInfo cacheField = providerType.GetField(
            "_renderedChunks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM cache field is missing.");
        FieldInfo lruField = providerType.GetField(
            "_cacheLru", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM LRU field is missing.");

        using var cancellation = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int callerThread = Environment.CurrentManagedThreadId;
        int workerThread = 0;
        int checkpoints = 0;
        Action waitForExternalCancellation = () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref checkpoints);
            reached.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("External cancellation checkpoint was not released.");
        };

        Task? task = null;
        try
        {
            // Start inside the first audible chunk, ensuring a real render.
            task = method.Invoke(provider,
                [5000L, cacheBudgetBytes, cancellation.Token, waitForExternalCancellation]) as Task
                ?? throw new InvalidOperationException("Async prerender did not return a Task.");
            if (!reached.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Async prerender never reached the PCM checkpoint.");

            // The caller cancels while the Task.Run worker is blocked at the checkpoint.
            if (workerThread == callerThread)
                throw new InvalidOperationException("Prerender checkpoint ran on the caller thread.");
            cancellation.Cancel();
            release.Set();
            try
            {
                task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Externally cancelled prerender unexpectedly succeeded.");
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation was observed before cache publication.
            }
        }
        finally
        {
            // Never leave a background worker waiting if an assertion fails.
            release.Set();
        }

        if (task is null || !task.IsCanceled || checkpoints != 1 ||
            workerThread == 0 || workerThread == callerThread ||
            provider.RenderChunkAttempts != 1 ||
            provider.CachedPcmBytes != 0 || provider.Metrics.RenderedChunkCount != 0 ||
            cacheField.GetValue(provider) is not IDictionary chunks || chunks.Count != 0 ||
            lruField.GetValue(provider) is not LinkedList<long> lru || lru.Count != 0)
            throw new InvalidOperationException("External cancellation published PCM or missed the worker checkpoint.");

        provider.PrerenderAheadAsync(5000L, cacheBudgetBytes, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (chunks.Count == 0 || lru.Count == 0 || provider.CachedPcmBytes == 0)
            throw new InvalidOperationException("Async prerender did not recover after external cancellation.");
    }


    // Verify cancellation after the post-render check, while another thread
    // owns the cache lock. The checkpoint does not prove Monitor contention.
    private static void VerifyExternalCancellationWithCacheLockContended(
        LevelDocument level, TimingMap timingMap, HitSoundTimeline timeline,
        Dictionary<string, RenderedHitSound> clips, long totalFrames,
        Type providerType, int sampleRate, long cacheBudgetBytes)
    {
        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level, timingMap, timeline, clips, totalFrames);
        MethodInfo method = providerType.GetMethod(
            "PrerenderAheadAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken), typeof(Action), typeof(Action)],
            modifiers: null)
            ?? throw new InvalidOperationException("Pre-lock prerender checkpoint is missing.");
        object cacheLock = providerType.GetField("_cacheLock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) ?? throw new InvalidOperationException("PCM cache lock is missing.");
        var chunks = providerType.GetField("_renderedChunks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) as IDictionary
            ?? throw new InvalidOperationException("PCM cache is missing.");
        var lru = providerType.GetField("_cacheLru", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) as LinkedList<long>
            ?? throw new InvalidOperationException("PCM LRU is missing.");

        using var cancellation = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int checkpoints = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int workerThread = 0;
        Action beforeLock = () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref checkpoints);
            reached.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Pre-lock checkpoint was not released.");
        };

        Task? task = null;
        try
        {
            task = method.Invoke(provider,
                [5000L, cacheBudgetBytes, cancellation.Token, null, beforeLock]) as Task
                ?? throw new InvalidOperationException("Async prerender did not return a Task.");
            if (!reached.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("PCM render did not reach the pre-lock checkpoint.");

            // The worker has passed the post-render cancellation check.
            // Hold the lock until cancellation is requested and the worker
            // is released, then verify the post-acquisition check rejects it.
            Monitor.Enter(cacheLock);
            try
            {
                release.Set();
                cancellation.Cancel();
            }
            finally
            {
                Monitor.Exit(cacheLock);
            }
            try
            {
                task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancelled prerender unexpectedly succeeded.");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
        finally
        {
            release.Set();
        }

        if (task is null || !task.IsCanceled || checkpoints != 1 ||
            workerThread == 0 || workerThread == callerThread ||
            provider.RenderChunkAttempts != 1 ||
            provider.CachedPcmBytes != 0 || provider.Metrics.RenderedChunkCount != 0 ||
            chunks.Count != 0 || lru.Count != 0)
            throw new InvalidOperationException("Pre-lock cancellation modified the PCM cache.");

        provider.PrerenderAheadAsync(5000L, cacheBudgetBytes, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (chunks.Count == 0 || lru.Count == 0 || provider.CachedPcmBytes == 0)
            throw new InvalidOperationException("PCM cache did not recover after pre-lock cancellation.");
    }

    private static void VerifyCancellationAfterRenderDoesNotPublishChunk(
        SampleAccurateHitSoundProvider provider, Type providerType)
    {
        MethodInfo ensureWithCheckpoint = providerType.GetMethod(
            "TryEnsureChunkWithinBudget", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long), typeof(long), typeof(CancellationToken), typeof(Action)],
            modifiers: null)
            ?? throw new InvalidOperationException("PCM pre-cache cancellation checkpoint is missing.");
        FieldInfo cacheField = providerType.GetField(
            "_renderedChunks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM chunk cache field is missing.");
        FieldInfo lruField = providerType.GetField(
            "_cacheLru", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PCM LRU field is missing.");
        if (cacheField.GetValue(provider) is not IDictionary chunks ||
            lruField.GetValue(provider) is not LinkedList<long> lru)
            throw new InvalidOperationException("PCM cache inspection failed.");

        // Floor 1 is at 5000 frames, inside chunk 1 (4096..8191).
        const long audibleChunk = 1;
        const long budget = 2L * 4096 * 2 * sizeof(float);
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0;
        Action cancelBeforeCache = () =>
        {
            checkpoints++;
            cancellation.Cancel();
        };
        try
        {
            ensureWithCheckpoint.Invoke(provider,
                [audibleChunk, budget, cancellation.Token, cancelBeforeCache]);
            throw new InvalidOperationException("Cancelled rendered PCM chunk was accepted.");
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is OperationCanceledException)
        {
            // Cancellation after rendering must prevent publication to cache.
        }
        if (checkpoints != 1 || chunks.Count != 0 || lru.Count != 0 ||
            provider.CachedPcmBytes != 0 || provider.Metrics.RenderedChunkCount != 0 ||
            provider.RenderChunkAttempts != 1)
            throw new InvalidOperationException("Cancelled rendered PCM changed cache or missed checkpoint.");

        // Cancellation must not poison later normal rendering of that chunk.
        object? result = ensureWithCheckpoint.Invoke(provider,
            [audibleChunk, budget, CancellationToken.None, null]);
        if (result is not true || !chunks.Contains(audibleChunk) ||
            lru.Count != 1 || provider.CachedPcmBytes != 4096L * 2 * sizeof(float))
            throw new InvalidOperationException("Normal prerender failed after cancellation.");
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

        // Cancel at the second 256-sample checkpoint, after actual writes.
        // The callback runs synchronously on the mixer thread, not a race-prone worker.
        MethodInfo probedMix = providerType.GetMethod(
            "AddScaled", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(float[]), typeof(int), typeof(float[]), typeof(int),
                    typeof(int), typeof(float), typeof(CancellationToken), typeof(Action<int>)],
            modifiers: null)
            ?? throw new InvalidOperationException("PCM mixer checkpoint test overload is missing.");
        float[] interrupted = Enumerable.Repeat(0.125f, samples).ToArray();
        using var midMixCancellation = new CancellationTokenSource();
        int checkpoint = -1;
        Action<int> cancelAtSecondCheckpoint = index =>
        {
            if (index == 256)
            {
                checkpoint = index;
                midMixCancellation.Cancel();
            }
        };
        try
        {
            probedMix.Invoke(null, [interrupted, 0, source, 0, samples, 0.5f,
                midMixCancellation.Token, cancelAtSecondCheckpoint]);
            throw new InvalidOperationException("Mid-mix cancellation unexpectedly completed.");
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is OperationCanceledException)
        {
            // The second checkpoint must abort before writing sample 256.
        }
        if (checkpoint != 256)
            throw new InvalidOperationException($"Mid-mix cancellation occurred at unexpected checkpoint {checkpoint}.");
        for (int i = 0; i < samples; i++)
        {
            float expected = i < 256 ? 0.125f + source[i] * 0.5f : 0.125f;
            if (interrupted[i] != expected)
                throw new InvalidOperationException($"Mid-mix cancellation wrote an unexpected sample at {i}.");
        }

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
