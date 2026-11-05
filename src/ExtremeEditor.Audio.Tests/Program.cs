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
