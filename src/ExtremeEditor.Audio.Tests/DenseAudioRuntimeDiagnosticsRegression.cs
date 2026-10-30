using System.Numerics;
using System.Reflection;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class DenseAudioRuntimeDiagnosticsRegression
{
    internal static void Run()
    {
        Type pcmType = typeof(SampleAccurateHitSoundProvider);
        MethodInfo? pcmSnapshot = pcmType.GetMethod("GetRealtimeDiagnostics",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Require(pcmSnapshot is not null,
            "PCM cache must expose foreground chunk-render latency and cache-miss counts, separate from background prerender.");

        Type mixType = typeof(UnifiedAudioSampleProvider);
        MethodInfo? mixSnapshot = mixType.GetMethod("GetRealtimeDiagnostics",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Require(mixSnapshot is not null,
            "The output graph must expose song-read time, hitsound-read time and master limiter gain.");

        const int sampleRate = 1000;
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        LevelDocument level = new()
        {
            SourcePath = "<dense-audio-diagnostics>",
            Angles = new double[65],
            Positions = new Vector2[66],
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

        var floors = Enumerable.Range(0, 66)
            .Select(i => new FloorTiming(i, i == 0 ? 0 : 0.1,
                i == 0 ? 0.1 : 0.101, 0, 0, Math.PI, 100, false, false))
            .ToArray();
        var timing = new TimingMap(floors);
        var timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound([0.5f, 0.5f, 0.5f, 0.5f], 0.0)
        };
        var pcm = new SampleAccurateHitSoundProvider(format, level, timing, timeline, clips, 500);
        var transport = new TransportHitSoundSampleProvider(pcm, 1.0);
        var graph = new UnifiedAudioSampleProvider(new ConstantSong(format, 0.25f), transport);

        float[] output = new float[440];
        graph.Read(output, 0, output.Length);

        object pcmStats = pcmSnapshot!.Invoke(pcm, null)
            ?? throw new InvalidOperationException("PCM diagnostics snapshot was null.");
        long foregroundMisses = (long)(pcmStats.GetType().GetProperty("ForegroundChunkRenders")?.GetValue(pcmStats)
            ?? throw new InvalidOperationException("PCM diagnostics must count foreground-only chunk renders."));
        Require(foregroundMisses > 0,
            "The first uncached dense Read must register its PCM chunk render as real-time work.");

        object mixStats = mixSnapshot!.Invoke(graph, null)
            ?? throw new InvalidOperationException("Audio graph diagnostics snapshot was null.");
        long readCalls = (long)(mixStats.GetType().GetProperty("ReadCalls")?.GetValue(mixStats)
            ?? throw new InvalidOperationException("Graph diagnostics must count Read callbacks."));
        double minGain = (double)(mixStats.GetType().GetProperty("MinimumMasterGain")?.GetValue(mixStats)
            ?? throw new InvalidOperationException("Graph diagnostics must report limiting severity."));
        Require(readCalls > 0 && minGain < 0.10,
            "Dense overlapping hits must be measurable as severe master limiting instead of silently losing the song.");

        string audioPlayer = FindSource("src/ExtremeEditor.Audio/AudioPlayer.cs");
        Require(audioPlayer.Contains("audio_player.realtime", StringComparison.Ordinal) &&
                audioPlayer.Contains("StartHeartbeat(", StringComparison.Ordinal),
            "AudioPlayer must publish aggregated runtime diagnostics on a background timer, never by synchronous logging in the audio Read callback.");
    }

    private static string FindSource(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string path = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new InvalidOperationException("RED setup failure: source unavailable: " + relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private sealed class ConstantSong(WaveFormat waveFormat, float value) : ISampleProvider
    {
        public WaveFormat WaveFormat => waveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
    }
}
