using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio.Tests;

internal static class ProgressiveAudioIntegrationBatchRegression
{
    private const int SampleRate = 1_000;
    private const int ChunkFrames = 4096;
    private const long ChunkPcmBytes = ChunkFrames * 2L * sizeof(float);

    [ModuleInitializer]
    public static void Run()
    {
        var failures = new List<string>();
        CheckForegroundBoundedCache(failures);
        CheckPrerenderLifecycle(failures);
        CheckAudioPlayerWiring(failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "BATCH RED:\n - " + string.Join("\n - ", failures));
        }
    }

    private static void CheckForegroundBoundedCache(List<string> failures)
    {
        try
        {
            var provider = CreateProvider([1.0, 5.0, 9.0, 13.0], totalFrames: 18_000);
            Type providerType = typeof(SampleAccurateHitSoundProvider);
            MethodInfo? setBudget = providerType.GetMethod(
                "SetCacheBudgetBytes",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(long)],
                modifiers: null);
            if (setBudget is null)
            {
                failures.Add("RED-A bounded cache: SampleAccurateHitSoundProvider.SetCacheBudgetBytes(long) does not exist.");
                return;
            }

            const long budget = 2 * ChunkPcmBytes;
            setBudget.Invoke(provider, [budget]);

            foreach (long frame in new long[] { 1_000, 5_000, 9_000, 13_000 })
            {
                provider.Seek(frame);
                var buffer = new float[2];
                provider.Read(buffer, 0, buffer.Length);
                Near(0.25f, buffer[0], $"bounded cache hit at frame {frame}");
                if (provider.CachedPcmBytes > budget)
                {
                    failures.Add(
                        $"RED-A bounded cache: foreground Read exceeded budget: cached={provider.CachedPcmBytes} budget={budget}.");
                    return;
                }
            }

            FieldInfo chunksField = providerType.GetField(
                "_renderedChunks",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_renderedChunks is unavailable.");
            if (chunksField.GetValue(provider) is not IDictionary chunks)
                throw new InvalidOperationException("_renderedChunks must expose IDictionary for regression inspection.");

            if (chunks.Contains(0L))
            {
                failures.Add("RED-A bounded cache: least-recently-used chunk 0 was not evicted after later foreground reads.");
                return;
            }

            provider.Seek(1_000);
            var reconstructed = new float[2];
            provider.Read(reconstructed, 0, reconstructed.Length);
            Near(0.25f, reconstructed[0], "bounded cache reconstructs evicted PCM");
            if (provider.CachedPcmBytes > budget)
            {
                failures.Add(
                    $"RED-A bounded cache: reconstructing an evicted chunk exceeded budget: cached={provider.CachedPcmBytes} budget={budget}.");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"RED-A bounded cache: {Unwrap(ex).Message}");
        }
    }

    private static void CheckPrerenderLifecycle(List<string> failures)
    {
        try
        {
            Assembly audioAssembly = typeof(AudioPlayer).Assembly;
            Type? lifecycleType = audioAssembly.GetType("ExtremeEditor.Audio.AudioPcmPrerenderLifecycle");
            if (lifecycleType is null)
            {
                failures.Add("RED-B lifecycle: ExtremeEditor.Audio.AudioPcmPrerenderLifecycle does not exist.");
                return;
            }

            MethodInfo? start = lifecycleType.GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(SampleAccurateHitSoundProvider), typeof(int), typeof(long), typeof(long)],
                modifiers: null);
            MethodInfo? restart = lifecycleType.GetMethod(
                "Restart",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(long)],
                modifiers: null);
            MethodInfo? cancel = lifecycleType.GetMethod(
                "Cancel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (start is null || restart is null || cancel is null)
            {
                failures.Add("RED-B lifecycle: Start(provider, sampleRate, startFrame, availableMemoryBytes), Restart(startFrame), and Cancel() are required.");
                return;
            }

            object lifecycle = Activator.CreateInstance(lifecycleType, nonPublic: true)
                ?? throw new InvalidOperationException("AudioPcmPrerenderLifecycle could not be constructed.");
            var provider = CreateProvider([0.1, 9.0], totalFrames: 14_000);

            // available / 5 = exactly one 4096-frame stereo float chunk.
            long availableMemoryBytes = ChunkPcmBytes * 5;
            start.Invoke(lifecycle, [provider, SampleRate, 0L, availableMemoryBytes]);

            if (!ContainsChunk(provider, 0L))
            {
                failures.Add("RED-B lifecycle: Start must synchronously prime the initial 0.5-second range before background prerender continues.");
                cancel.Invoke(lifecycle, null);
                return;
            }

            restart.Invoke(lifecycle, [9_000L]);
            if (!ContainsChunk(provider, 2L))
            {
                failures.Add("RED-B lifecycle: Restart must prime the seek target range under the same bounded cache budget.");
                cancel.Invoke(lifecycle, null);
                return;
            }

            cancel.Invoke(lifecycle, null);
        }
        catch (Exception ex)
        {
            failures.Add($"RED-B lifecycle: {Unwrap(ex).Message}");
        }
    }

    private static void CheckAudioPlayerWiring(List<string> failures)
    {
        try
        {
            Type playerType = typeof(AudioPlayer);
            BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            bool hasProvider = playerType.GetFields(fields)
                .Any(field => field.FieldType == typeof(SampleAccurateHitSoundProvider));
            bool hasLifecycle = playerType.GetFields(fields)
                .Any(field => field.FieldType.FullName == "ExtremeEditor.Audio.AudioPcmPrerenderLifecycle");
            MethodInfo? restart = playerType.GetMethod(
                "RestartHitSoundPrerender",
                fields,
                binder: null,
                types: [typeof(long)],
                modifiers: null);
            MethodInfo? cancel = playerType.GetMethod(
                "CancelHitSoundPrerender",
                fields,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (!hasProvider || !hasLifecycle || restart is null || cancel is null)
            {
                failures.Add(
                    "RED-C AudioPlayer wiring: AudioPlayer must retain the hitsound provider + prerender lifecycle and expose internal RestartHitSoundPrerender(long)/CancelHitSoundPrerender() helpers for BuildGraph/Seek/Dispose wiring.");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"RED-C AudioPlayer wiring: {Unwrap(ex).Message}");
        }
    }

    private static SampleAccurateHitSoundProvider CreateProvider(
        IReadOnlyList<double> hitTimesSeconds,
        long totalFrames)
    {
        var timings = new FloorTiming[hitTimesSeconds.Count + 1];
        timings[0] = Floor(0, 0.0);
        for (int i = 0; i < hitTimesSeconds.Count; i++)
            timings[i + 1] = Floor(i + 1, hitTimesSeconds[i]);

        LevelDocument level = CreateLevel(timings.Length);
        var timingMap = new TimingMap(timings);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.25f, 0.25f], 0.0)
        };

        return new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2),
            level,
            timingMap,
            timeline,
            clips,
            totalFrames);
    }

    private static bool ContainsChunk(SampleAccurateHitSoundProvider provider, long chunkIndex)
    {
        FieldInfo field = typeof(SampleAccurateHitSoundProvider).GetField(
            "_renderedChunks",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_renderedChunks is unavailable.");
        return field.GetValue(provider) is IDictionary chunks && chunks.Contains(chunkIndex);
    }

    private static FloorTiming Floor(int floor, double entry) =>
        new(floor, entry, entry + 1.0 / SampleRate, 0, 0, Math.PI, 100, false, false);

    private static LevelDocument CreateLevel(int floors) => new()
    {
        SourcePath = "<progressive-audio-batch-test>",
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

    private static void Near(float expected, float actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.00001f)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
}
