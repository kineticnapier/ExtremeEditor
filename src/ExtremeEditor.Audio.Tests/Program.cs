using System.Diagnostics;
using System.Numerics;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

if (string.Equals(Environment.GetEnvironmentVariable("EXTREMEEDITOR_AUDIO_DENSE_ONLY"),
    "1", StringComparison.Ordinal))
{
    DenseAudioRuntimeDiagnosticsRegression.Run();
    DenseTransportBlockMixRegression.Run();
    Console.WriteLine("PASS: dense audio diagnostics and transport block mixing.");
    return 0;
}

DenseAudioRuntimeDiagnosticsRegression.Run();
DenseTransportBlockMixRegression.Run();
PcmWaveReaderRegression.Run();
MutedPrerenderRegression.Run();
MutedTransportRegression.Run();
RunSampleIndexChecks();
RunDiagnosticLogChecks();
RunDiagnosticHeartbeatChecks();
RunClockChecks();
RunSongPitchRateChecks();
RunNonFiniteSongVolumeChecks();
RunNonFiniteHitSoundVolumeChecks();
RunMutedHitScheduleChecks();
RunEmptyHitScheduleCacheChecks();
RunSparsePrerenderCacheChecks();
RunSparsePrerenderGapChecks();
RunSparsePrimeRangeChecks();
RunSparseForegroundSilenceCacheChecks();
RunLongClipSparseCandidateIndexChecks();
RunProviderChecks();
RunPlaySoundEventChecks();
RunHitSoundScheduleHotSwapRegression();
MultichannelFoldDownRegression.Run();
RunOffsetOrderingRegression();
RunNegativeStartTailRegression();
RunProgressiveConstructionRegression();
RunSparseTimelineChecks();
RunMillionFloorScanBenchmark();
Console.WriteLine("All audio foundation checks passed.");
return 0;

static void RunDiagnosticLogChecks()
{
    using var writer = new StringWriter();
    var log = new AudioDiagnosticLog(writer);
    using (log.Measure("test.phase", "provider=finite"))
        log.Write("test.read", "iteration=1 read=128 cumulative=128");

    string output = writer.ToString();
    Contains("BEGIN phase=test.phase", output, "diagnostic phase begin");
    Contains("END phase=test.phase", output, "diagnostic phase end");
    Contains("event=test.read", output, "diagnostic event");
    Contains("thread=", output, "diagnostic thread id");
}

static void RunDiagnosticHeartbeatChecks()
{
    using var writer = new StringWriter();
    var log = new AudioDiagnosticLog(writer);
    using IDisposable heartbeat = log.StartHeartbeat(
        "test.heartbeat",
        TimeSpan.FromMilliseconds(10),
        () => "iteration=1 cumulative=128");

    bool emittedWhileCallerWaited = SpinWait.SpinUntil(
        () => writer.ToString().Contains("event=test.heartbeat", StringComparison.Ordinal),
        TimeSpan.FromSeconds(1));
    if (!emittedWhileCallerWaited)
        throw new InvalidOperationException("diagnostic heartbeat was not emitted from its timer");
}

static void RunSampleIndexChecks()
{
    Equal(48_000L, SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(1.0, 48_000), "1.000000 s");
    Equal(48_024L, SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(1.0005, 48_000), "1.000500 s");
    Equal(48_048L, SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(1.001, 48_000), "1.001000 s");
}

static void RunClockChecks()
{
    LevelDocument level = CreateLevel(2);
    level = WithSettings(level, offsetMilliseconds: 250, pitchPercent: 100, countdownTicks: 4,
        separateCountdownTime: true, initialBpm: 120);
    double audio = PlaybackClock.ChartToAudioTime(level, 2.0);
    Near(0.25, audio, "separate countdown chart-to-audio");
    Near(2.0, PlaybackClock.AudioToChartTime(level, audio), "clock round trip");
}

static void RunNonFiniteSongVolumeChecks()
{
    LevelDocument level = CreateLevel(2);
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 1.0)]);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);

    foreach (double volume in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        var plan = new OfflineAudioRenderPlan(level, timing, timeline, null, 0.0, 1.0, volume);
        try
        {
            plan.Validate();
            throw new InvalidOperationException("Non-finite offline song volume was accepted.");
        }
        catch (ArgumentOutOfRangeException ex) when (
            ex.ParamName == nameof(OfflineAudioRenderPlan.SongVolumePercent))
        {
            // Invalid volume must be rejected before any PCM rendering begins.
        }

        var song = new AudioGraphFactory.FiniteSilentSampleProvider(format) { TotalFrames = 1_000 };
        try
        {
            _ = AudioGraphFactory.Build(
                song, null, null, null, null, 1_000, volume, 1.0, false);
            throw new InvalidOperationException("Non-finite audio graph volume was accepted.");
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "songVolumePercent")
        {
            // Preview and export must not propagate NaN into audio samples.
        }
    }

    foreach (double volume in new[] { 0.0, 100.0 })
    {
        new OfflineAudioRenderPlan(level, timing, timeline, null, 0.0, 1.0, volume)
            .Validate();
    }
}

static void RunNonFiniteHitSoundVolumeChecks()
{
    const int sampleRate = 1_000;
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    var timing = new TimingMap([
        Floor(0, 0.0),
        Floor(1, 0.001),
        Floor(2, 0.002),
        Floor(3, 0.003)
    ]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };

    foreach (double invalidVolume in new[]
    {
        double.NaN, double.PositiveInfinity, double.NegativeInfinity
    })
    {
        LevelDocument level = CreateLevel(4, new Dictionary<int, LevelAction[]>
        {
            [2] = [new LevelAction(2, "SetHitsound", true, null, null, null, null)
            {
                HitSoundVolumePercent = invalidVolume
            }],
            [3] = [new LevelAction(3, "PlaySound", true, null, null, null, null)
            {
                HitSound = "Kick",
                HitSoundVolumePercent = invalidVolume
            }]
        });
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        Near(0.0, timeline.GetStateAtFloor(2).Volume,
            "non-finite SetHitsound state is normalized at timeline construction");
        LevelDocument invalidInitial = CreateLevel(2, defaultVolumePercent: invalidVolume);
        Near(0.0, HitSoundTimelineBuilder.Build(invalidInitial).InitialState.Volume,
            "non-finite initial hit-sound state is normalized");

        var provider = new SampleAccurateHitSoundProvider(
            format, level, timing, timeline, clips, 8);
        var samples = new float[8];
        provider.Read(samples, 0, samples.Length);

        if (samples.Any(sample => !float.IsFinite(sample)))
            throw new InvalidOperationException("Non-finite hit-sound volume leaked into PCM.");
        Near(0.5, samples[2], "normal hit-sound volume remains unchanged");
        Near(0.0, samples[4], "invalid SetHitsound volume is muted");
        Near(0.0, samples[6], "invalid PlaySound volume is muted");
    }
}

static void RunMutedHitScheduleChecks()
{
    const int sampleRate = 1_000;
    LevelDocument level = CreateLevel(4, new Dictionary<int, LevelAction[]>
    {
        [2] = [new LevelAction(2, "SetHitsound", true, null, null, null, null)
        {
            HitSoundVolumePercent = 100
        }],
        [3] =
        [
            new LevelAction(3, "SetHitsound", true, null, null, null, null)
            {
                HitSoundVolumePercent = 0
            },
            new LevelAction(3, "PlaySound", true, null, null, null, null)
            {
                HitSound = "Kick",
                HitSoundVolumePercent = 0
            }
        ]
    }, defaultVolumePercent: 0);

    var timing = new TimingMap([
        Floor(0, 0.0), Floor(1, 0.001), Floor(2, 0.002), Floor(3, 0.003)
    ]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, 8);

    Equal(4, provider.Metrics.SourceHitCount,
        "muted floor and PlaySound events remain counted as source hits");
    Equal(1, provider.Metrics.ScheduledHitCount,
        "only audible hits occupy the PCM schedule");

    var samples = new float[16];
    provider.Read(samples, 0, samples.Length);
    Near(0.0, samples[1 * 2], "muted initial floor is silent");
    Near(0.5, samples[2 * 2], "unmuted floor remains audible");
    Near(0.0, samples[3 * 2], "muted SetHitsound and PlaySound are silent");
}

static void RunEmptyHitScheduleCacheChecks()
{
    const int sampleRate = 1_000;
    const long totalFrames = 16_384;
    LevelDocument level = CreateLevel(4, defaultVolumePercent: 0);
    var timing = new TimingMap([
        Floor(0, 0.0), Floor(1, 0.001), Floor(2, 0.002), Floor(3, 0.003)
    ]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);

    Equal(3, provider.Metrics.SourceHitCount, "muted floors remain in diagnostics");
    Equal(0, provider.Metrics.ScheduledHitCount, "all-muted schedule is empty");

    provider.PrimeRange(0, totalFrames);
    provider.PrerenderAheadAsync(0, 128 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();

    var samples = Enumerable.Repeat(1f, 1_024).ToArray();
    Equal(samples.Length, provider.Read(samples, 0, samples.Length),
        "muted read returns the requested sample count");
    Equal(512L, provider.PositionFrames, "muted read advances the sample clock");
    if (samples.Any(sample => sample != 0f))
        throw new InvalidOperationException("muted read must overwrite the buffer with silence");

    Equal(0, provider.Metrics.RenderedChunkCount,
        "all-muted playback must not create empty PCM cache entries");
    Equal(0L, provider.CachedPcmBytes,
        "all-muted playback must not allocate cached PCM");
}

static void RunSparsePrerenderCacheChecks()
{
    // A single audible hit followed by many silent chunks must not fill the
    // speculative LRU with zero-byte sentinels (which still use managed memory).
    const int sampleRate = 1_000;
    const long totalFrames = 32L * 4096;
    LevelDocument level = CreateLevel(2);
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 0.001)]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);

    provider.PrerenderAheadAsync(0, 64 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();

    Equal(1, provider.Metrics.RenderedChunkCount,
        "sparse background prerender retains only the audible chunk");
    Equal(1L, provider.RenderChunkAttempts,
        "sparse background prerender skips silent chunk rendering");
    Equal(4096L * 2 * sizeof(float), provider.CachedPcmBytes,
        "sparse background prerender PCM cache size");

    provider.Seek(1);
    var hitPcm = new float[2];
    Equal(2, provider.Read(hitPcm, 0, 2), "sparse cached hit read");
    Near(0.5, hitPcm[0], "sparse cached hit sample");

    provider.Seek(10L * 4096);
    var silentPcm = new[] { 1f, 1f };
    Equal(2, provider.Read(silentPcm, 0, 2), "sparse silent region read");
    Near(0.0, silentPcm[0], "sparse silent region remains silent");
    Near(0.0, silentPcm[1], "sparse silent region right channel");
}

static void RunSparsePrerenderGapChecks()
{
    // A clip crossing a chunk boundary needs both chunks. A second hit much
    // later must not cause every intervening silent chunk to be rendered.
    const int sampleRate = 1_000;
    const long totalFrames = 64L * 4096;
    LevelDocument level = CreateLevel(3);
    var timing = new TimingMap([
        Floor(0, 0.0),
        Floor(1, 4.095),
        Floor(2, 200.001)
    ]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound(Enumerable.Repeat(0.5f, 12).ToArray(), 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);

    provider.PrerenderAheadAsync(0, 128 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();

    Equal(3L, provider.RenderChunkAttempts,
        "sparse background prerender renders only the three audible chunks");
    Equal(3, provider.Metrics.RenderedChunkCount,
        "sparse background prerender retains all audible chunks");
    Equal(3L * 4096 * 2 * sizeof(float), provider.CachedPcmBytes,
        "sparse background prerender PCM bytes");

    provider.Seek(4095);
    var crossing = new float[12];
    Equal(12, provider.Read(crossing, 0, crossing.Length),
        "cross-boundary hit read");
    if (crossing.Any(sample => Math.Abs(sample - 0.5f) > 1e-6f))
        throw new InvalidOperationException("cross-boundary hit samples differ");

    provider.Seek(200_001);
    var later = new float[2];
    Equal(2, provider.Read(later, 0, later.Length), "later hit read");
    Near(0.5, later[0], "later hit left channel");
    Near(0.5, later[1], "later hit right channel");

    var fromBoundary = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);
    fromBoundary.PrerenderAheadAsync(4096, 128 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal(2L, fromBoundary.RenderChunkAttempts,
        "seeked prerender retains an earlier hit tail and a later hit");
    fromBoundary.Seek(4096);
    var tail = new float[2];
    Equal(2, fromBoundary.Read(tail, 0, 2), "seeked tail read");
    Near(0.5, tail[0], "seeked tail remains audible");

    var nearEnd = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);
    nearEnd.PrerenderAheadAsync(200_000, 128 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal(1L, nearEnd.RenderChunkAttempts,
        "late seek prerenders only the remaining audible chunk");

    // A speculative cache budget that holds exactly one audible chunk must
    // stop BEFORE rendering the next chunk, not after wasting a full mix.
    const long oneChunkBytes = 4096L * 2 * sizeof(float);
    var budgetLimited = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);
    budgetLimited.PrerenderAheadAsync(0, oneChunkBytes, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal(1L, budgetLimited.RenderChunkAttempts,
        "full speculative cache skips the next expensive chunk render");
    Equal(1, budgetLimited.Metrics.RenderedChunkCount,
        "speculative cache retains only the first chunk within budget");
    Equal(oneChunkBytes, budgetLimited.CachedPcmBytes,
        "speculative cache never exceeds its supplied budget");
    budgetLimited.Seek(4096);
    var uncachedTail = new float[2];
    Equal(2, budgetLimited.Read(uncachedTail, 0, 2),
        "foreground still renders an uncached tail after prerender stops");
    Near(0.5, uncachedTail[0], "uncached tail remains audible");
}

static void RunSparsePrimeRangeChecks()
{
    // Priming a long sparse chart must not retain an empty sentinel for
    // every silent chunk. A hit crossing a boundary still needs both chunks.
    const int sampleRate = 1_000;
    const long totalFrames = 64L * 4096;
    LevelDocument level = CreateLevel(3);
    var timing = new TimingMap([
        Floor(0, 0.0),
        Floor(1, 4.095),
        Floor(2, 200.001)
    ]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound(Enumerable.Repeat(0.5f, 12).ToArray(), 0.0)
    };
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);

    var full = new SampleAccurateHitSoundProvider(
        format, level, timing, timeline, clips, totalFrames);
    full.PrimeRange(0, totalFrames);
    Equal(3L, full.RenderChunkAttempts,
        "sparse priming renders only the three audible chunks");
    Equal(3, full.Metrics.RenderedChunkCount,
        "sparse priming caches only audible chunks");
    Equal(3L * 4096 * 2 * sizeof(float), full.CachedPcmBytes,
        "sparse priming PCM bytes");
    full.Seek(4096);
    var tail = new float[2];
    Equal(2, full.Read(tail, 0, 2), "primed boundary tail read");
    Near(0.5, tail[0], "primed boundary tail remains audible");

    var fromBoundary = new SampleAccurateHitSoundProvider(
        format, level, timing, timeline, clips, totalFrames);
    fromBoundary.PrimeRange(4096, 4096);
    Equal(1L, fromBoundary.RenderChunkAttempts,
        "priming from a boundary includes the earlier hit tail");
    fromBoundary.Seek(4096);
    var boundaryTail = new float[2];
    Equal(2, fromBoundary.Read(boundaryTail, 0, 2),
        "boundary-only primed tail read");
    Near(0.5, boundaryTail[0], "boundary-only primed tail sample");

    var silentGap = new SampleAccurateHitSoundProvider(
        format, level, timing, timeline, clips, totalFrames);
    silentGap.PrimeRange(100_000, 1_000);
    Equal(0L, silentGap.RenderChunkAttempts,
        "priming an entirely silent gap does no rendering");
    Equal(0, silentGap.Metrics.RenderedChunkCount,
        "silent-gap priming does not cache empty chunks");

    // When the requested prime range exceeds the RAM budget, keep the earliest
    // audible chunk instead of evicting it to prime later chunks. The first
    // playback callback must not pay to render the same chunk again.
    const long oneChunkBudget = 4096L * 2 * sizeof(float);
    var budgetLimited = new SampleAccurateHitSoundProvider(
        format, level, timing, timeline, clips, totalFrames);
    budgetLimited.SetCacheBudgetBytes(oneChunkBudget);
    budgetLimited.PrimeRange(0, totalFrames);
    Equal(1L, budgetLimited.RenderChunkAttempts,
        "budgeted priming renders only the first audible chunk");
    Equal(1, budgetLimited.Metrics.RenderedChunkCount,
        "budgeted priming retains the first audible chunk");
    Equal(oneChunkBudget, budgetLimited.CachedPcmBytes,
        "budgeted priming stays within the PCM cache limit");
    budgetLimited.Seek(4095);
    var firstHit = new float[2];
    Equal(2, budgetLimited.Read(firstHit, 0, firstHit.Length),
        "budgeted priming first-hit read");
    Near(0.5, firstHit[0], "budgeted priming first-hit left");
    Near(0.5, firstHit[1], "budgeted priming first-hit right");
    Equal(1L, budgetLimited.RenderChunkAttempts,
        "first playback callback reuses the primed chunk");
}

static void RunSparseForegroundSilenceCacheChecks()
{
    // A sparse chart has an audible hit, so normal foreground reads still
    // inspect silent chunks. Their zero-byte sentinels must not accumulate
    // unbounded dictionary/LRU metadata during a long playback or seek.
    const int sampleRate = 1_000;
    const long totalFrames = 512L * 4096;
    LevelDocument level = CreateLevel(2);
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 0.001)]);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);

    Equal(1, provider.Metrics.ScheduledHitCount,
        "sparse foreground fixture must contain an audible hit");

    for (int chunk = 1; chunk <= 384; chunk++)
    {
        provider.Seek(chunk * 4096L);
        var samples = new[] { 1f, 1f };
        Equal(2, provider.Read(samples, 0, samples.Length),
            "sparse foreground silent read count");
        Near(0.0, samples[0], "sparse foreground silence left");
        Near(0.0, samples[1], "sparse foreground silence right");
    }

    Equal(0, provider.Metrics.RenderedChunkCount,
        "foreground silence must not accumulate zero-byte cache entries");
    Equal(0L, provider.CachedPcmBytes,
        "foreground silence must not allocate cached PCM");
    Equal(0L, provider.RenderChunkAttempts,
        "foreground silence must bypass rendering, not just avoid caching");

    provider.Seek(1);
    var hitSamples = new float[2];
    Equal(2, provider.Read(hitSamples, 0, hitSamples.Length),
        "sparse foreground audible read count");
    Near(0.5, hitSamples[0], "sparse foreground audible left");
    Near(0.5, hitSamples[1], "sparse foreground audible right");
    Equal(1, provider.Metrics.RenderedChunkCount,
        "audible foreground chunks must still be cached");
    Equal(1L, provider.RenderChunkAttempts,
        "audible foreground hit still renders once");
}

static void RunLongClipSparseCandidateIndexChecks()
{
    // A future long clip must not force every silent middle chunk to inspect
    // already-finished short hits. Its tail must still mix with later hits.
    const int sampleRate = 1_000;
    const int shortHitCount = 96;
    const long totalFrames = 850_000;
    var timings = new FloorTiming[shortHitCount + 3];
    timings[0] = Floor(0, 0.0);
    for (int floor = 1; floor <= shortHitCount; floor++)
        timings[floor] = Floor(floor, floor * 2.0);
    timings[shortHitCount + 1] = Floor(shortHitCount + 1, 500.0);
    timings[shortHitCount + 2] = Floor(shortHitCount + 2, 600.0);

    LevelDocument level = CreateLevel(timings.Length, new Dictionary<int, LevelAction[]>
    {
        [shortHitCount + 1] = [new LevelAction(shortHitCount + 1, "SetHitsound", true, null, null, null, null)
        {
            HitSound = "Long"
        }],
        [shortHitCount + 2] = [new LevelAction(shortHitCount + 2, "SetHitsound", true, null, null, null, null)
        {
            HitSound = "Kick"
        }]
    });
    var timing = new TimingMap(timings);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.25f, 0.25f], 0.0),
        ["Long"] = new RenderedHitSound(Enumerable.Repeat(0.5f, 300_000 * 2).ToArray(), 0.0)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level, timing, HitSoundTimelineBuilder.Build(level), clips, totalFrames);

    Equal(shortHitCount + 2, provider.ScheduledHits.Count, "long-clip sparse schedule");
    Equal(shortHitCount, provider.FirstPotentiallyOverlappingHitIndex(350_000),
        "silent gap skips all expired short hits");
    Equal(shortHitCount, provider.FirstPotentiallyOverlappingHitIndex(600_000),
        "long tail remains a candidate during later hit");
    Equal(shortHitCount + 2, provider.FirstPotentiallyOverlappingHitIndex(800_000),
        "expired long tail is excluded at its exact end");

    // Priming and background prerendering must use the same end-frame index
    // as foreground reads, including silent seeks and the exclusive tail end.
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    SampleAccurateHitSoundProvider NewProvider() => new(
        format, level, timing, timeline, clips, totalFrames);

    var silentPrime = NewProvider();
    silentPrime.PrimeRange(350_000, 4096);
    Equal(0L, silentPrime.RenderChunkAttempts,
        "silent middle-range priming skips all expired hits");

    var tailPrime = NewProvider();
    tailPrime.PrimeRange(600_000, 1);
    Equal(1L, tailPrime.RenderChunkAttempts,
        "priming a long tail and later short hit renders their shared chunk once");
    tailPrime.Seek(600_000);
    var primedTail = new float[2];
    Equal(2, tailPrime.Read(primedTail, 0, 2), "primed long-tail read count");
    Near(0.75, primedTail[0], "primed long-tail overlap left");
    Near(0.75, primedTail[1], "primed long-tail overlap right");

    // Seeking to the exact end still visits the containing chunk because
    // that chunk includes earlier tail samples; the next chunk is fully silent.
    var boundaryPrerender = NewProvider();
    boundaryPrerender.PrerenderAheadAsync(800_000, 32 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal(1L, boundaryPrerender.RenderChunkAttempts,
        "background prerender keeps the chunk crossing the exclusive tail end");
    boundaryPrerender.Seek(800_000);
    var boundaryPcm = new float[2];
    Equal(2, boundaryPrerender.Read(boundaryPcm, 0, 2),
        "exclusive tail end read count");
    Near(0.0, boundaryPcm[0], "exclusive tail end left");
    Near(0.0, boundaryPcm[1], "exclusive tail end right");

    var expiredPrerender = NewProvider();
    expiredPrerender.PrerenderAheadAsync(802_816, 32 * 1024, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal(0L, expiredPrerender.RenderChunkAttempts,
        "background prerender skips the first fully silent chunk after the tail");
    Equal(0L, expiredPrerender.CachedPcmBytes,
        "expired background prerender leaves no PCM cache");

    void CheckFrame(long frame, double expected, string label)
    {
        provider.Seek(frame);
        var pcm = new[] { -1f, -1f };
        Equal(2, provider.Read(pcm, 0, pcm.Length), label + " read");
        Near(expected, pcm[0], label + " left");
        Near(expected, pcm[1], label + " right");
    }

    CheckFrame(350_000, 0.0, "silent gap before future long hit");
    Equal(0, provider.Metrics.RenderedChunkCount, "silent gap leaves cache empty");
    Equal(0L, provider.RenderChunkAttempts,
        "silent gap before a future long hit does not render");
    CheckFrame(500_000, 0.5, "long clip begins");
    CheckFrame(600_000, 0.75, "short hit mixes with long clip tail");
    CheckFrame(799_999, 0.5, "last frame of long clip");
    CheckFrame(800_000, 0.0, "long clip ends exclusively");
}

static void RunSongPitchRateChecks()
{
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(1_000, 2);
    var silent = new AudioGraphFactory.FiniteSilentSampleProvider(format)
    {
        TotalFrames = 2_000
    };
    AudioGraphBuildResult graph = AudioGraphFactory.Build(
        silent,
        level: null,
        timingMap: null,
        timeline: null,
        clips: null,
        totalFrames: 2_000,
        songVolumePercent: 100,
        playbackSpeed: 0.5,
        hitSoundsEnabled: false,
        songPitch: 1.5);

    Near(0.75, graph.SongRateProvider.PlaybackSpeed,
        "song pitch and user speed compose only on the song branch");
    Equal(1_000L, AudioGraphFactory.CalculatePlaybackFrames(
        songFrames: 2_000,
        sampleRate: 1_000,
        level: null,
        timingMap: null,
        clips: null,
        songPitch: 2.0),
        "pitch shortens decoded-song transport duration");
}

static void RunProviderChecks()
{
    const int sampleRate = 48_000;
    const long totalFrames = sampleRate * 2L;
    LevelDocument level = CreateLevel(4);
    var floors = new[]
    {
        Floor(0, 0.0),
        Floor(1, 1.0),
        Floor(2, 1.0005),
        Floor(3, 1.001)
    };
    var timing = new TimingMap(floors);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    float[] pcm = Enumerable.Repeat(0.25f, 200).ToArray(); // 100 stereo frames
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound(pcm, 0.0)
    };
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    var provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames);
    provider.Seek(47_990);
    var buffer = new float[200];
    provider.Read(buffer, 0, buffer.Length);
    Near(0.0, buffer[9 * 2], "silence before exact sample");
    Near(0.25, buffer[10 * 2], "floor 1 exact sample");
    Near(0.5, buffer[34 * 2], "floor 2 exact sample overlap");
    Near(0.75, buffer[58 * 2], "floor 3 exact sample overlap");

    provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames);
    provider.Seek(47_990);
    var firstChunk = new float[80]; // ends at frame 48030
    var secondChunk = new float[80];
    provider.Read(firstChunk, 0, firstChunk.Length);
    provider.Read(secondChunk, 0, secondChunk.Length);
    Near(0.5, secondChunk[0], "tail crosses provider chunk boundary");

    provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, totalFrames);
    provider.Seek(48_010);
    var seekBuffer = new float[20];
    provider.Read(seekBuffer, 0, seekBuffer.Length);
    Near(0.25, seekBuffer[0], "seek reconstructs an audible tail");
}

static void RunPlaySoundEventChecks()
{
    const int sampleRate = 1_000;
    var active = new LevelAction(1, "PlaySound", true, null, null, null, null)
    {
        HitSound = "Hat",
        HitSoundVolumePercent = 50,
        AngleOffset = 90
    };
    var inactive = new LevelAction(1, "PlaySound", false, null, null, null, null)
    {
        HitSound = "Hat",
        HitSoundVolumePercent = 100
    };
    LevelDocument level = CreateLevel(2, new Dictionary<int, LevelAction[]>
    {
        [1] = [active, inactive]
    });
    var timing = new TimingMap([
        new FloorTiming(0, 0.0, 1.0, 0, 0, Math.PI, 100, false, false),
        new FloorTiming(1, 1.0, 2.0, 0, 0, Math.PI, 100, false, false)
    ]);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.25f, 0.25f], 0.0),
        ["Hat"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };

    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level,
        timing,
        timeline,
        clips,
        totalFrames: 2_000);

    Equal(2, provider.ScheduledHits.Count,
        "PlaySound adds one independent cue and ignores inactive events");
    SampleAccurateHitSoundProvider.ScheduledHit eventHit = provider.ScheduledHits
        .Single(hit => ReferenceEquals(hit.Clip, clips["Hat"]));
    Equal(1_300L, eventHit.StartFrame, "PlaySound angleOffset sample frame");
    Near(0.5, eventHit.Volume, "PlaySound volume");

    // Verify audible PCM, not just the schedule: the event must survive seeking
    // and remain independent of the ordinary floor landing sound.
    var landingPcm = new float[2];
    provider.Seek(1_000);
    Equal(2, provider.Read(landingPcm, 0, landingPcm.Length), "PlaySound landing read");
    Near(0.25, landingPcm[0], "ordinary landing PCM");

    var eventPcm = new float[2];
    provider.Seek(1_300);
    Equal(2, provider.Read(eventPcm, 0, eventPcm.Length), "PlaySound event read after seek");
    Near(0.25, eventPcm[0], "PlaySound event left PCM");
    Near(0.25, eventPcm[1], "PlaySound event right PCM");

    // A chart pitch of 200% must advance both landing and PlaySound cues
    // on the same transport clock, without changing their relative volume.
    LevelDocument pitchedLevel = WithSettings(
        level, offsetMilliseconds: 0, pitchPercent: 200,
        countdownTicks: 0, separateCountdownTime: false, initialBpm: 100);
    var pitchedProvider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        pitchedLevel,
        timing,
        HitSoundTimelineBuilder.Build(pitchedLevel),
        clips,
        totalFrames: 2_000);
    SampleAccurateHitSoundProvider.ScheduledHit pitchedEvent = pitchedProvider.ScheduledHits
        .Single(hit => ReferenceEquals(hit.Clip, clips["Hat"]));
    Equal(650L, pitchedEvent.StartFrame, "PlaySound chart pitch sample frame");
    var pitchedPcm = new float[2];
    pitchedProvider.Seek(650);
    Equal(2, pitchedProvider.Read(pitchedPcm, 0, pitchedPcm.Length), "pitched PlaySound read");
    Near(0.25, pitchedPcm[0], "pitched PlaySound PCM");

    // Exercise the complete preview/offline audio graph, not just the
    // scheduled hit-sound layer. Pitch must affect the song branch while
    // the landing and PlaySound remain aligned on the output sample clock.
    var silentSong = new AudioGraphFactory.FiniteSilentSampleProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2))
    {
        TotalFrames = 2_000
    };
    AudioGraphBuildResult fullGraph = AudioGraphFactory.Build(
        silentSong,
        pitchedLevel,
        timing,
        HitSoundTimelineBuilder.Build(pitchedLevel),
        clips,
        totalFrames: 2_000,
        songVolumePercent: 100,
        playbackSpeed: 1.0,
        hitSoundsEnabled: true,
        songPitch: 2.0);
    Near(2.0, fullGraph.SongRateProvider.PlaybackSpeed,
        "PlaySound full graph song pitch");
    var mixedPcm = new float[1_400]; // 700 stereo output frames
    Equal(mixedPcm.Length, fullGraph.Graph.Read(mixedPcm, 0, mixedPcm.Length),
        "PlaySound full graph read");
    Near(0.25, mixedPcm[500 * 2], "PlaySound full graph landing PCM");
    Near(0.25, mixedPcm[650 * 2], "PlaySound full graph event PCM");
    Near(0.0, mixedPcm[649 * 2], "PlaySound full graph pre-event silence");
}

static void RunHitSoundScheduleHotSwapRegression()
{
    const int sampleRate = 1_000;
    WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    LevelDocument level = CreateLevel(2);
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 0.010)]);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kick"] = new RenderedHitSound([0.5f, 0.5f], 0.0)
    };

    var firstSchedule = new SampleAccurateHitSoundProvider(
        format, level, timing, timeline, clips, 100);
    var graph = new UnifiedAudioSampleProvider(
        new AudioGraphFactory.FiniteSilentSampleProvider(format) { TotalFrames = 100 },
        new TransportHitSoundSampleProvider(firstSchedule, playbackSpeed: 1.0));

    var before = new float[20];
    graph.Read(before, 0, before.Length);
    Near(0.0, before[^2], "old schedule before hot swap");

    var movedTiming = new TimingMap([Floor(0, 0.0), Floor(1, 0.020)]);
    var movedSchedule = new SampleAccurateHitSoundProvider(
        format, level, movedTiming, timeline, clips, 100);
    graph.ReplaceTransportHitSounds(
        new TransportHitSoundSampleProvider(movedSchedule, playbackSpeed: 1.0),
        sourceFrame: 10);

    var after = new float[22];
    graph.Read(after, 0, after.Length);
    Near(0.0, after[18], "replacement schedule before moved hit");
    Near(0.5, after[20], "replacement schedule moved hit");

    string playerSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory, "src", "ExtremeEditor.Audio", "AudioPlayer.cs"));
    int methodStart = playerSource.IndexOf(
        "private void RefreshHitSoundGraphPreservingDevice()", StringComparison.Ordinal);
    if (methodStart < 0)
        throw new InvalidOperationException("#65: AudioPlayer has no device-preserving hitsound refresh path.");
    int methodEnd = playerSource.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
    string method = playerSource[methodStart..(methodEnd < 0 ? playerSource.Length : methodEnd)];
    if (method.Contains("BuildGraph(", StringComparison.Ordinal) ||
        method.Contains("_output.Dispose", StringComparison.Ordinal) ||
        method.Contains("_output.Init", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "#65: hitsound refresh still rebuilds or reinitializes the WaveOut device.");
    }
}

static void RunSparseTimelineChecks()
{
    LevelDocument level = CreateLevel(4, new Dictionary<int, LevelAction[]>
    {
        [2] = [new LevelAction(2, "SetHitsound", true, null, null, null, null)
        {
            HitSoundVolumePercent = 25
        }],
        [3] = [new LevelAction(3, "SetHitsound", true, null, null, null, null)
        {
            HitSound = "None"
        }]
    });
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    Equal(2, timeline.StateChangeCount, "sparse SetHitsound count");
    Equal("Kick", timeline.GetStateAtFloor(1).Name, "initial hitsound");
    Equal("Kick", timeline.GetStateAtFloor(2).Name, "volume-only SetHitsound retains name");
    Near(0.25, timeline.GetStateAtFloor(2).Volume, "SetHitsound volume");
    Equal("None", timeline.GetStateAtFloor(3).Name, "SetHitsound name");
}

static void RunOffsetOrderingRegression()
{
    const int sampleRate = 1_000;
    LevelDocument level = CreateLevel(3, new Dictionary<int, LevelAction[]>
    {
        [2] = [new LevelAction(2, "SetHitsound", true, null, null, null, null)
        {
            HitSound = "B"
        }]
    }, defaultHitSound: "A");
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 1.0), Floor(2, 1.001)]);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = new RenderedHitSound(Enumerable.Repeat(0.25f, 8).ToArray(), 0.0),
        ["B"] = new RenderedHitSound(Enumerable.Repeat(0.5f, 8).ToArray(), 0.002)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), level, timing, timeline, clips, 2_000);

    // Floor 1 starts at frame 1000, but floor 2's larger offset moves it back
    // to frame 999. A break based on floor 1's clip-specific start loses floor 2.
    provider.Seek(998);
    var firstRead = new float[4]; // frames [998, 1000)
    provider.Read(firstRead, 0, firstRead.Length);
    Near(0.0, firstRead[0], "offset inversion silence before later floor");
    Near(0.5, firstRead[2], "offset inversion schedules later floor first");

    var secondRead = new float[4]; // frames [1000, 1002)
    provider.Read(secondRead, 0, secondRead.Length);
    Near(0.75, secondRead[0], "pending earlier floor survives offset-bound scan");
}

static void RunNegativeStartTailRegression()
{
    const int sampleRate = 1_000;
    LevelDocument level = CreateLevel(2, defaultHitSound: "Negative");
    var timing = new TimingMap([Floor(0, 0.0), Floor(1, 0.001)]);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    float[] pcm = [0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f, 0.5f, 0.5f];
    var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
    {
        ["Negative"] = new RenderedHitSound(pcm, 0.003)
    };
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), level, timing, timeline, clips, 1_000);

    // The clip starts at frame -2. Frames 0..2 must use source frames 2..4.
    var buffer = new float[6];
    provider.Read(buffer, 0, buffer.Length);
    Near(0.3, buffer[0], "negative start skips pre-zero prefix");
    Near(0.4, buffer[2], "negative start keeps tail frame 1");
    Near(0.5, buffer[4], "negative start keeps tail frame 2");
}

static void RunProgressiveConstructionRegression()
{
    const int sampleRate = 1_000;
    const int hitCount = 64;
    const double hitSpacingSeconds = 5.0; // > one 4096-frame renderer chunk

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
    long totalFrames = checked((long)((hitCount + 1) * hitSpacingSeconds * sampleRate));

    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
        level,
        timingMap,
        timeline,
        clips,
        totalFrames);

    if (provider.Metrics.RenderedChunkCount != 0)
    {
        throw new InvalidOperationException(
            $"RED: provider construction must build schedule/work metadata only; " +
            $"it eagerly rendered {provider.Metrics.RenderedChunkCount} PCM chunks.");
    }
}

static void RunMillionFloorScanBenchmark()
{
    const int floorCount = 1_000_000;
    const int sampleRate = 48_000;
    var timings = new FloorTiming[floorCount];
    for (int i = 0; i < timings.Length; i++)
        timings[i] = Floor(i, i / (double)sampleRate);

    LevelDocument level = CreateLevel(floorCount);
    var timingMap = new TimingMap(timings);
    HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
    var provider = new SampleAccurateHitSoundProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), level, timingMap, timeline,
        new Dictionary<string, RenderedHitSound>(), floorCount + 8192L);
    var buffer = new float[8192];
    var watch = Stopwatch.StartNew();
    while (provider.PositionFrames <= floorCount)
        provider.Read(buffer, 0, buffer.Length);
    watch.Stop();
    Console.WriteLine($"million-floor streamed cue scan: {watch.Elapsed.TotalMilliseconds:F1} ms; timeline states: {timeline.StateChangeCount}");
}

static FloorTiming Floor(int floor, double entry) =>
    new(floor, entry, entry + 1.0 / 48_000, 0, 0, Math.PI, 100, false, false);

static LevelDocument CreateLevel(
    int floors,
    IReadOnlyDictionary<int, LevelAction[]>? actions = null,
    string defaultHitSound = "Kick",
    double defaultVolumePercent = 100.0) => new()
{
    SourcePath = "<test>",
    Angles = new double[Math.Max(0, floors - 1)],
    Positions = new Vector2[floors],
    ActionCount = actions?.Values.Sum(value => value.Length) ?? 0,
    ActionTypeCounts = actions is null
        ? new Dictionary<string, int>()
        : new Dictionary<string, int>
        {
            ["SetHitsound"] = actions.Values.SelectMany(value => value)
                .Count(action => action.Kind == LevelActionKind.SetHitsound)
        },
    ActionsByFloor = actions ?? new Dictionary<int, LevelAction[]>(),
    InitialBpm = 100,
    SongFilename = null,
    OffsetMilliseconds = 0,
    PitchPercent = 100,
    CountdownTicks = 0,
    SeparateCountdownTime = false,
    DefaultHitSound = defaultHitSound,
    HitSoundVolumePercent = defaultVolumePercent,
    Bounds = default
};

static LevelDocument WithSettings(LevelDocument source, double offsetMilliseconds, double pitchPercent,
    int countdownTicks, bool separateCountdownTime, double initialBpm) => new()
{
    SourcePath = source.SourcePath,
    Angles = source.Angles,
    Positions = source.Positions,
    ActionCount = source.ActionCount,
    ActionTypeCounts = source.ActionTypeCounts,
    ActionsByFloor = source.ActionsByFloor,
    InitialBpm = initialBpm,
    SongFilename = source.SongFilename,
    OffsetMilliseconds = offsetMilliseconds,
    PitchPercent = pitchPercent,
    CountdownTicks = countdownTicks,
    SeparateCountdownTime = separateCountdownTime,
    DefaultHitSound = source.DefaultHitSound,
    HitSoundVolumePercent = source.HitSoundVolumePercent,
    Bounds = source.Bounds
};

static void Equal<T>(T expected, T actual, string name) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}

static void Near(double expected, double actual, string name)
{
    if (Math.Abs(expected - actual) > 0.00001)
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}

static void Contains(string expected, string actual, string name)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: expected to find '{expected}' in '{actual}'");
}
