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
        if (!double.IsFinite(StartAudioTimeSeconds))
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

public readonly record struct OfflineAudioFormat(
    int SampleRate,
    int Channels);

public sealed class OfflineAudioRenderer
{
    private readonly HitSoundLibrary _hitSoundLibrary = new();

    public OfflineAudioRenderer()
    {
        _hitSoundLibrary.ReloadAssets();
    }

    public OfflineAudioFormat ProbeFormat(string? songPath)
    {
        string? resolved = string.IsNullOrWhiteSpace(songPath)
            ? null
            : Path.GetFullPath(songPath);

        if (resolved is null || !File.Exists(resolved))
        {
            return new OfflineAudioFormat(
                AudioGraphFactory.DefaultHitSoundOnlySampleRate,
                2);
        }

        using WaveStream reader = string.Equals(
            Path.GetExtension(resolved),
            ".ogg",
            StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(resolved)
            : new AudioFileReader(resolved);
        ISampleProvider song = AudioGraphFactory.NormalizeSong(reader.ToSampleProvider());
        return new OfflineAudioFormat(
            song.WaveFormat.SampleRate,
            song.WaveFormat.Channels);
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

            long requestedStartFrame =
                SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
                    plan.StartAudioTimeSeconds,
                    sampleRate);
            long requestedEndFrame =
                SampleAccurateHitSoundProvider.AudioTimeToSampleFrame(
                    plan.EndAudioTimeSeconds,
                    sampleRate);
            if (requestedEndFrame <= requestedStartFrame)
                return;

            long sourceStartFrame = Math.Clamp(requestedStartFrame, 0, totalFrames);
            if (reader is not null)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(
                    Math.Min(
                        Math.Max(0.0, plan.StartAudioTimeSeconds),
                        reader.TotalTime.TotalSeconds));
            }
            silentSong?.Seek(sourceStartFrame);

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
            graph.Seek(sourceStartFrame);

            int channels = graph.WaveFormat.Channels;
            int chunkSamples = checked(plan.ChunkFrames * channels);
            float[] readBuffer = new float[chunkSamples];
            long currentFrame = requestedStartFrame;

            while (currentFrame < requestedEndFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int framesRequested = checked((int)Math.Min(
                    plan.ChunkFrames,
                    requestedEndFrame - currentFrame));
                int samplesRequested = checked(framesRequested * channels);
                int completeSamples;

                if (currentFrame < 0)
                {
                    int silentFrames = checked((int)Math.Min(
                        framesRequested,
                        -currentFrame));
                    completeSamples = checked(silentFrames * channels);
                    Array.Clear(readBuffer, 0, completeSamples);
                }
                else if (currentFrame >= totalFrames)
                {
                    completeSamples = samplesRequested;
                    Array.Clear(readBuffer, 0, completeSamples);
                }
                else
                {
                    int sourceFrames = checked((int)Math.Min(
                        framesRequested,
                        totalFrames - currentFrame));
                    int sourceSamples = checked(sourceFrames * channels);
                    int samplesRead = graph.Read(readBuffer, 0, sourceSamples);
                    if (samplesRead < sourceSamples)
                    {
                        Array.Clear(
                            readBuffer,
                            Math.Max(0, samplesRead),
                            sourceSamples - Math.Max(0, samplesRead));
                    }

                    completeSamples = sourceSamples;
                }

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
