using System.Numerics;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class PostSongHitScheduleRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        const int sampleRate = 1_000;
        const long shortSongFrames = 1_000;
        LevelDocument level = CreateLevel();
        var timingMap = new TimingMap([
            Floor(0, 0.0),
            Floor(1, 0.5),
            Floor(2, 2.0)]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.5f, 0.5f, 0.25f, 0.25f], 0.0)
        };

        long extended = AudioGraphFactory.CalculatePlaybackFrames(
            shortSongFrames, sampleRate, level, timingMap, clips);
        if (extended <= 2_000)
            throw new InvalidOperationException("RED: playback must extend beyond a short song through the final hit tail");

        var provider = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level,
            timingMap,
            timeline,
            clips,
            extended);
        if (!provider.ScheduledHits.Any(hit => hit.StartFrame >= shortSongFrames))
            throw new InvalidOperationException("RED: a floor after song EOF must remain in the hit schedule");

        const long longSongFrames = 5_000;
        if (AudioGraphFactory.CalculatePlaybackFrames(
                longSongFrames, sampleRate, level, timingMap, clips) != longSongFrames)
            throw new InvalidOperationException("A longer song must retain its existing playback duration");
    }

    private static FloorTiming Floor(int floor, double entry) =>
        new(floor, entry, entry + 0.001, 0, 0, Math.PI, 100, false, false);

    private static LevelDocument CreateLevel() => new()
    {
        SourcePath = "<post-song-hit-test>",
        Angles = new double[2],
        Positions = new Vector2[3],
        ActionCount = 0,
        ActionTypeCounts = new Dictionary<string, int>(),
        ActionsByFloor = new Dictionary<int, LevelAction[]>(),
        InitialBpm = 100,
        SongFilename = "short.wav",
        OffsetMilliseconds = 0,
        PitchPercent = 100,
        CountdownTicks = 0,
        SeparateCountdownTime = false,
        DefaultHitSound = "Kick",
        HitSoundVolumePercent = 100,
        Bounds = default
    };
}
