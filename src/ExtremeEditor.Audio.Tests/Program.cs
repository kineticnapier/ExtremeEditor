using System.Diagnostics;
using System.Numerics;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

RunSampleIndexChecks();
RunDiagnosticLogChecks();
RunDiagnosticHeartbeatChecks();
RunClockChecks();
RunProviderChecks();
RunOffsetOrderingRegression();
RunNegativeStartTailRegression();
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

static void RunProviderChecks()
{
    const int sampleRate = 48_000;
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

    var provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips);
    provider.Seek(47_990);
    var buffer = new float[200];
    provider.Read(buffer, 0, buffer.Length);
    Near(0.0, buffer[9 * 2], "silence before exact sample");
    Near(0.25, buffer[10 * 2], "floor 1 exact sample");
    Near(0.5, buffer[34 * 2], "floor 2 exact sample overlap");
    Near(0.75, buffer[58 * 2], "floor 3 exact sample overlap");

    provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips);
    provider.Seek(47_990);
    var firstChunk = new float[80]; // ends at frame 48030
    var secondChunk = new float[80];
    provider.Read(firstChunk, 0, firstChunk.Length);
    provider.Read(secondChunk, 0, secondChunk.Length);
    Near(0.5, secondChunk[0], "tail crosses provider chunk boundary");

    provider = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips);
    provider.Seek(48_010);
    var seekBuffer = new float[20];
    provider.Read(seekBuffer, 0, seekBuffer.Length);
    Near(0.25, seekBuffer[0], "seek reconstructs an audible tail");
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
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), level, timing, timeline, clips);

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
        WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), level, timing, timeline, clips);

    // The clip starts at frame -2. Frames 0..2 must use source frames 2..4.
    var buffer = new float[6];
    provider.Read(buffer, 0, buffer.Length);
    Near(0.3, buffer[0], "negative start skips pre-zero prefix");
    Near(0.4, buffer[2], "negative start keeps tail frame 1");
    Near(0.5, buffer[4], "negative start keeps tail frame 2");
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
        new Dictionary<string, RenderedHitSound>());
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
    string defaultHitSound = "Kick") => new()
{
    SourcePath = "<test>",
    Angles = new double[Math.Max(0, floors - 1)],
    Positions = new Vector2[floors],
    ActionCount = actions?.Values.Sum(value => value.Length) ?? 0,
    ActionTypeCounts = new Dictionary<string, int>(),
    ActionsByFloor = actions ?? new Dictionary<int, LevelAction[]>(),
    InitialBpm = 100,
    SongFilename = null,
    OffsetMilliseconds = 0,
    PitchPercent = 100,
    CountdownTicks = 0,
    SeparateCountdownTime = false,
    DefaultHitSound = defaultHitSound,
    HitSoundVolumePercent = 100,
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
