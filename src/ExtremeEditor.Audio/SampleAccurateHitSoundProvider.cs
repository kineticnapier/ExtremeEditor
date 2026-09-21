using System.Diagnostics;
using System.Numerics;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

internal readonly record struct HitSoundRenderMetrics(
    TimeSpan BuildSchedule,
    TimeSpan RenderChunks,
    int SourceHitCount,
    int ScheduledHitCount,
    int RenderedChunkCount);

/// <summary>
/// Serves a prerendered hit-sound PCM layer on the same absolute sample clock as
/// the song. Floor/timing/timeline/name lookup and overlapping-voice mixing are
/// completed in the constructor so the realtime callback only copies PCM chunks.
/// </summary>
internal sealed class SampleAccurateHitSoundProvider : ISampleProvider
{
    private const int ChunkFrames = 4096;

    private readonly Dictionary<long, float[]> _renderedChunks;
    private long _positionFrames;

    public SampleAccurateHitSoundProvider(
        WaveFormat waveFormat,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips,
        long totalFrames)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat || waveFormat.Channels != 2)
            throw new ArgumentException("The hit-sound renderer requires stereo IEEE float audio.", nameof(waveFormat));

        WaveFormat = waveFormat;

        var watch = Stopwatch.StartNew();
        ScheduleBuildResult schedule = BuildSchedule(
            waveFormat.SampleRate,
            level,
            timingMap,
            timeline,
            clips,
            Math.Max(0, totalFrames));
        watch.Stop();
        TimeSpan buildSchedule = watch.Elapsed;

        watch.Restart();
        _renderedChunks = RenderChunks(schedule.Hits, Math.Max(0, totalFrames));
        watch.Stop();

        Metrics = new HitSoundRenderMetrics(
            buildSchedule,
            watch.Elapsed,
            schedule.SourceHitCount,
            schedule.Hits.Count,
            _renderedChunks.Count);
        Seek(0);
    }

    public WaveFormat WaveFormat { get; }
    internal long PositionFrames => _positionFrames;
    internal HitSoundRenderMetrics Metrics { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frameCount = count / WaveFormat.Channels;
        int sampleCount = frameCount * WaveFormat.Channels;
        Array.Clear(buffer, offset, sampleCount);
        if (frameCount == 0)
            return 0;

        // Deliberately do not clamp this bus. Overlapping hit sounds must remain
        // linear until the song and hit-sound buses reach the single master limiter.
        CopyRenderedPcm(buffer, offset, _positionFrames, frameCount);
        _positionFrames += frameCount;
        return sampleCount;
    }

    public void Seek(long positionFrames)
    {
        _positionFrames = Math.Max(0, positionFrames);
    }

    internal static long AudioTimeToSampleFrame(double audioSeconds, int sampleRate) =>
        checked((long)Math.Round(audioSeconds * sampleRate, MidpointRounding.AwayFromZero));

    private static ScheduleBuildResult BuildSchedule(
        int sampleRate,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips,
        long totalFrames)
    {
        if (clips.Count == 0 || totalFrames <= 0)
            return new ScheduleBuildResult([], 0);

        int floorCount = Math.Min(level.FloorCount, timingMap.Floors.Count);
        if (floorCount <= 1)
            return new ScheduleBuildResult([], 0);

        IReadOnlyList<FloorTiming> timings = timingMap.Floors;
        IReadOnlyList<HitSoundStateChange> changes = timeline.Changes;
        int changeIndex = 0;
        HitSoundState state = timeline.InitialState;
        ResolveState(state, clips, out RenderedHitSound? currentClip, out float currentVolume);

        // Do not reserve one element per floor: ultra-high-BPM charts collapse
        // many landings onto the same sample frame before they reach this list.
        var hits = new List<ScheduledHit>(Math.Min(floorCount - 1, 262_144));
        int sourceHitCount = 0;

        RenderedHitSound? pendingClip = null;
        long pendingStartFrame = 0;
        float pendingVolume = 0f;

        for (int floor = 1; floor < floorCount; floor++)
        {
            while (changeIndex < changes.Count && changes[changeIndex].Floor <= floor)
            {
                state = changes[changeIndex].State;
                changeIndex++;
                ResolveState(state, clips, out currentClip, out currentVolume);
            }

            FloorTiming timing = timings[floor];
            if (timing.MidSpin || currentClip is null)
                continue;

            double floorAudio = PlaybackClock.ChartToAudioTime(level, timing.EntryTime);
            double startAudio = floorAudio - currentClip.OffsetSeconds;
            long startFrame = AudioTimeToSampleFrame(startAudio, sampleRate);
            long endFrame = startFrame + currentClip.FrameCount;
            if (endFrame <= 0 || startFrame >= totalFrames)
                continue;

            sourceHitCount++;

            // At extreme BPM several floors often round to the exact same sample.
            // Linear mixing lets those identical clip/frame contributions collapse
            // into one scaled voice without changing their timing or waveform.
            if (pendingClip is not null &&
                ReferenceEquals(pendingClip, currentClip) &&
                pendingStartFrame == startFrame)
            {
                pendingVolume += currentVolume;
                continue;
            }

            if (pendingClip is not null)
                hits.Add(new ScheduledHit(pendingClip, pendingStartFrame, pendingVolume));

            pendingClip = currentClip;
            pendingStartFrame = startFrame;
            pendingVolume = currentVolume;
        }

        if (pendingClip is not null)
            hits.Add(new ScheduledHit(pendingClip, pendingStartFrame, pendingVolume));

        return new ScheduleBuildResult(hits, sourceHitCount);
    }

    private static void ResolveState(
        HitSoundState state,
        IReadOnlyDictionary<string, RenderedHitSound> clips,
        out RenderedHitSound? clip,
        out float volume)
    {
        volume = (float)Math.Clamp(state.Volume, 0.0, 1.0);
        if (string.Equals(state.Name, "None", StringComparison.OrdinalIgnoreCase))
        {
            clip = null;
            return;
        }

        string name = HitSoundLibrary.NormalizeName(state.Name);
        clips.TryGetValue(name, out clip);
    }

    private static Dictionary<long, float[]> RenderChunks(
        IReadOnlyList<ScheduledHit> hits,
        long totalFrames)
    {
        if (hits.Count == 0 || totalFrames <= 0)
            return [];

        // Build sparse, independent chunk jobs first. Each output chunk is then
        // owned by exactly one worker, so the expensive PCM mixing needs no locks.
        var workByChunk = new Dictionary<long, List<HitSlice>>();
        foreach (ScheduledHit hit in hits)
            AddHitSlices(workByChunk, hit, totalFrames);

        if (workByChunk.Count == 0)
            return [];

        KeyValuePair<long, List<HitSlice>>[] work = workByChunk.ToArray();
        var rendered = new float[work.Length][];

        void RenderChunk(int index)
        {
            var chunk = new float[ChunkFrames * 2];
            foreach (HitSlice slice in work[index].Value)
            {
                AddScaled(
                    chunk,
                    slice.DestinationSample,
                    slice.Clip.Samples,
                    slice.SourceSample,
                    slice.SampleCount,
                    slice.Volume);
            }
            rendered[index] = chunk;
        }

        if (work.Length < 4 || Environment.ProcessorCount <= 1)
        {
            for (int i = 0; i < work.Length; i++)
                RenderChunk(i);
        }
        else
        {
            Parallel.For(0, work.Length, RenderChunk);
        }

        var chunks = new Dictionary<long, float[]>(work.Length);
        for (int i = 0; i < work.Length; i++)
            chunks.Add(work[i].Key, rendered[i]);
        return chunks;
    }

    private static void AddHitSlices(
        Dictionary<long, List<HitSlice>> workByChunk,
        ScheduledHit hit,
        long totalFrames)
    {
        long sourceFrame = Math.Max(0, -hit.StartFrame);
        while (sourceFrame < hit.Clip.FrameCount)
        {
            long destinationFrame = hit.StartFrame + sourceFrame;
            if (destinationFrame < 0)
            {
                sourceFrame++;
                continue;
            }
            if (destinationFrame >= totalFrames)
                break;

            long chunkIndex = destinationFrame / ChunkFrames;
            int frameInChunk = (int)(destinationFrame % ChunkFrames);
            int availableInChunk = ChunkFrames - frameInChunk;
            long audibleFrames = totalFrames - destinationFrame;
            int framesToMix = (int)Math.Min(
                Math.Min((long)availableInChunk, hit.Clip.FrameCount - sourceFrame),
                audibleFrames);
            if (framesToMix <= 0)
                break;

            if (!workByChunk.TryGetValue(chunkIndex, out List<HitSlice>? slices))
            {
                slices = new List<HitSlice>(32);
                workByChunk.Add(chunkIndex, slices);
            }

            slices.Add(new HitSlice(
                hit.Clip,
                checked((int)sourceFrame) * 2,
                frameInChunk * 2,
                framesToMix * 2,
                hit.Volume));

            sourceFrame += framesToMix;
        }
    }

    private static void AddScaled(
        float[] destination,
        int destinationOffset,
        float[] source,
        int sourceOffset,
        int count,
        float volume)
    {
        int i = 0;
        int vectorWidth = Vector<float>.Count;
        var scale = new Vector<float>(volume);
        int vectorEnd = count - count % vectorWidth;

        for (; i < vectorEnd; i += vectorWidth)
        {
            var destinationVector = new Vector<float>(destination, destinationOffset + i);
            var sourceVector = new Vector<float>(source, sourceOffset + i);
            (destinationVector + sourceVector * scale).CopyTo(destination, destinationOffset + i);
        }

        for (; i < count; i++)
            destination[destinationOffset + i] += source[sourceOffset + i] * volume;
    }

    private void CopyRenderedPcm(float[] buffer, int offset, long startFrame, int frameCount)
    {
        long frame = startFrame;
        int destinationSample = offset;
        int framesRemaining = frameCount;

        while (framesRemaining > 0)
        {
            long chunkIndex = frame / ChunkFrames;
            int frameInChunk = (int)(frame % ChunkFrames);
            int framesToCopy = Math.Min(framesRemaining, ChunkFrames - frameInChunk);
            int samplesToCopy = framesToCopy * 2;

            if (_renderedChunks.TryGetValue(chunkIndex, out float[]? chunk))
            {
                Array.Copy(
                    chunk,
                    frameInChunk * 2,
                    buffer,
                    destinationSample,
                    samplesToCopy);
            }

            frame += framesToCopy;
            destinationSample += samplesToCopy;
            framesRemaining -= framesToCopy;
        }
    }

    private readonly record struct ScheduleBuildResult(List<ScheduledHit> Hits, int SourceHitCount);
    private readonly record struct ScheduledHit(RenderedHitSound Clip, long StartFrame, float Volume);
    private readonly record struct HitSlice(
        RenderedHitSound Clip,
        int SourceSample,
        int DestinationSample,
        int SampleCount,
        float Volume);
}
