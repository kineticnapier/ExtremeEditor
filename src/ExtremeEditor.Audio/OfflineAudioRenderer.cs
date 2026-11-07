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
        // Non-finite volume would propagate NaN into the exported PCM samples.
        if (!double.IsFinite(SongVolumePercent))
            throw new ArgumentOutOfRangeException(nameof(SongVolumePercent));
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
    // A muted export must not even initialize the shared hit-sound asset cache.
    private readonly Lazy<HitSoundLibrary> _hitSoundLibrary = new(() => SharedHitSoundLibrary.Instance);

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

        using WaveStream reader = SongReaderFactory.Open(resolved);
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
                reader = SongReaderFactory.Open(songPath);
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
            // Two different audio times can round to the same output frame.
            // Avoid initializing hit-sound assets or building an audio graph
            // when this interval cannot produce even one PCM frame.
            // Finite times can still overflow a 64-bit sample index after
            // multiplication by the source rate. Reject them as invalid
            // export ranges instead of leaking an OverflowException.
            if (!SampleAccurateHitSoundProvider.TryAudioTimeToSampleFrame(
                    plan.StartAudioTimeSeconds, sampleRate, out long requestedStartFrame))
                throw new ArgumentOutOfRangeException(
                    nameof(plan.StartAudioTimeSeconds), plan.StartAudioTimeSeconds,
                    "The export start time exceeds the representable sample range.");
            if (!SampleAccurateHitSoundProvider.TryAudioTimeToSampleFrame(
                    plan.EndAudioTimeSeconds, sampleRate, out long requestedEndFrame))
                throw new ArgumentOutOfRangeException(
                    nameof(plan.EndAudioTimeSeconds), plan.EndAudioTimeSeconds,
                    "The export end time exceeds the representable sample range.");
            if (requestedEndFrame <= requestedStartFrame)
                return;

            // Video exports have a fixed hit-sound setting for their entire
            // lifetime. Unlike live playback, they do not need a dormant hit
            // schedule or decoded assets when hitsounds are disabled.
            IReadOnlyDictionary<string, RenderedHitSound>? clips = null;
            if (plan.HitSoundsEnabled)
            {
                clips = AudioGraphFactory.RenderHitSoundAssets(
                    _hitSoundLibrary.Value,
                    sampleRate,
                    plan.Level,
                    plan.TimingMap,
                    plan.HitSoundTimeline,
                    out _);
            }

            long songFrames = reader is null
                ? 0
                : checked((long)Math.Ceiling(reader.TotalTime.TotalSeconds * sampleRate));
            long totalFrames = AudioGraphFactory.CalculatePlaybackFrames(
                songFrames,
                sampleRate,
                plan.Level,
                plan.TimingMap,
                clips,
                SongRatePolicy.FromPitchPercent(plan.Level.PitchPercent));

            if (silentSong is not null)
                silentSong.TotalFrames = totalFrames;

            long sourceStartFrame = Math.Clamp(requestedStartFrame, 0, totalFrames);
            double fractionalSongFrame = 0.0;
            if (reader is not null)
            {
                // A pitch-adjusted output frame can land between two source
                // frames. Seek to the integral source frame and retain the
                // fractional phase for the interpolation provider.
                double songPitch = SongRatePolicy.FromPitchPercent(plan.Level.PitchPercent);
                int sourceBlockAlign = reader.WaveFormat.BlockAlign;
                long sourceFrameCount = reader.Length / sourceBlockAlign;
                double sourcePosition = Math.Min(
                    (double)sourceStartFrame * songPitch, sourceFrameCount);
                long integralSourceFrame = (long)Math.Floor(sourcePosition);
                fractionalSongFrame = sourcePosition - integralSourceFrame;
                reader.Position = checked(integralSourceFrame * sourceBlockAlign);
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
                plan.HitSoundsEnabled,
                songPitch: SongRatePolicy.FromPitchPercent(plan.Level.PitchPercent));

            if (reader is not null)
                graphBuild.SongRateProvider.SetInitialPhase(fractionalSongFrame);

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
