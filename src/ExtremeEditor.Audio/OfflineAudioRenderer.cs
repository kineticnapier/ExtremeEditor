using ExtremeEditor.Core;
using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

public sealed record OfflineAudioRenderPlan(
    LevelDocument Level,
    TimingMap TimingMap,
    HitSoundTimeline HitSoundTimeline,
    string? SongPath,
    double StartAudioTimeSeconds,
    double EndAudioTimeSeconds,
    double SongVolumePercent = 100.0,
    bool HitSoundsEnabled = true,
    int ChunkFrames = 4096)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Level);
        ArgumentNullException.ThrowIfNull(TimingMap);
        ArgumentNullException.ThrowIfNull(HitSoundTimeline);
        if (!double.IsFinite(StartAudioTimeSeconds) || StartAudioTimeSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(StartAudioTimeSeconds));
        if (!double.IsFinite(EndAudioTimeSeconds) ||
            EndAudioTimeSeconds <= StartAudioTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(EndAudioTimeSeconds));
        if (ChunkFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChunkFrames));
    }
}

public sealed record OfflineAudioChunk(
    long StartFrame,
    int SampleRate,
    int Channels,
    float[] Samples)
{
    public int FrameCount => Samples.Length / Channels;
}

public sealed class OfflineAudioRenderer
{
    private readonly HitSoundLibrary _hitSoundLibrary = new();

    public OfflineAudioRenderer()
    {
        _hitSoundLibrary.ReloadAssets();
    }

    public async ValueTask RenderAsync(
        OfflineAudioRenderPlan plan,
        Func<OfflineAudioChunk, CancellationToken, ValueTask> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(consumer);
        plan.Validate();

        cancellationToken.ThrowIfCancellationRequested();

        WaveStream? reader = null;
        try
        {
            ISampleProvider song;
            AudioGraphFactory.FiniteSilentSampleProvider? silentSong = null;

            string? songPath = string.IsNullOrWhiteSpace(plan.SongPath)
                ? null
                : Path.GetFullPath(plan.SongPath);
            if (songPath is not null && File.Exists(songPath))
            {
                reader = string.Equals(
                    Path.GetExtension(songPath),
                    ".ogg",
                    StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(songPath)
                    : new AudioFileReader(songPath);
                song = AudioGraphFactory.NormalizeSong(reader.ToSampleProvider());
            }
            else
            {
                WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(
                    AudioGraphFactory.DefaultHitSoundOnlySampleRate,
                    2);
                silentSong = new AudioGraphFactory.FiniteSilentSampleProvider(format);
                song = silentSong;
            }

            int sampleRate = song.WaveFormat.SampleRate;
            IReadOnlyDictionary<string, RenderedHitSound>? clips =
                AudioGraphFactory.RenderHitSoundAssets(
                    _hitSoundLibrary,
                    sampleRate,
                    plan.Level,
                    plan.TimingMap,
                    plan.HitSoundTimeline,
                    out _);

            long totalFrames = reader is not null
                ? checked((long)Math.Ceiling(reader.TotalTime.TotalSeconds * sampleRate))
                : AudioGraphFactory.CalculateHitSoundOnlyFrames(
                    sampleRate,
                    plan.Level,
                    plan.TimingMap,
                    clips);

            if (silentSong is not null)
                silentSong.TotalFrames = totalFrames;

            long startFrame = SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
                plan.StartAudioTimeSeconds,
                sampleRate);
            long requestedEndFrame = SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
                plan.EndAudioTimeSeconds,
                sampleRate);
            long endFrame = Math.Min(totalFrames, requestedEndFrame);
            if (endFrame <= startFrame)
                return;

            if (reader is not null)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(
                    Math.Min(plan.StartAudioTimeSeconds, reader.TotalTime.TotalSeconds));
            }
            silentSong?.Seek(startFrame);

            AudioGraphBuildResult graphBuild = AudioGraphFactory.Build(
                song,
                plan.Level,
                plan.TimingMap,
                plan.HitSoundTimeline,
                clips,
                totalFrames,
                plan.SongVolumePercent,
                playbackSpeed: 1.0,
                plan.HitSoundsEnabled);

            UnifiedAudioSampleProvider graph = graphBuild.Graph;
            graph.Seek(startFrame);

            int channels = graph.WaveFormat.Channels;
            int chunkSamples = checked(plan.ChunkFrames * channels);
            float[] readBuffer = new float[chunkSamples];
            long currentFrame = startFrame;

            while (currentFrame < endFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int framesRequested = checked((int)Math.Min(
                    plan.ChunkFrames,
                    endFrame - currentFrame));
                int samplesRequested = checked(framesRequested * channels);
                int samplesRead = graph.Read(readBuffer, 0, samplesRequested);
                if (samplesRead <= 0)
                    break;

                int completeSamples = samplesRead - samplesRead % channels;
                if (completeSamples <= 0)
                    break;

                float[] chunkSamplesCopy = new float[completeSamples];
                Array.Copy(readBuffer, chunkSamplesCopy, completeSamples);
                var chunk = new OfflineAudioChunk(
                    currentFrame,
                    sampleRate,
                    channels,
                    chunkSamplesCopy);

                cancellationToken.ThrowIfCancellationRequested();
                await consumer(chunk, cancellationToken).ConfigureAwait(false);
                currentFrame += chunk.FrameCount;
            }
        }
        finally
        {
            reader?.Dispose();
        }
    }
}
