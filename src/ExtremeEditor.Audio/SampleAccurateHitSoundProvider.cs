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
/// Serves a sample-accurate hit-sound PCM layer on the same absolute sample clock
/// as the song. Construction precomputes only the compact hit schedule; PCM chunks
/// are rendered into a reusable RAM cache only when primed or first requested.
/// </summary>
internal sealed class SampleAccurateHitSoundProvider : ISampleProvider
{
    private const int ChunkFrames = 4096;

    private readonly List<ScheduledHit> _scheduledHits;
    private readonly Dictionary<long, float[]> _renderedChunks = [];
    private readonly object _cacheLock = new();
    private readonly long _totalFrames;
    private readonly long _maxClipFrames;
    private readonly TimeSpan _buildSchedule;
    private readonly int _sourceHitCount;
    private TimeSpan _renderChunksElapsed;
    private long _cachedPcmBytes;
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
        _totalFrames = Math.Max(0, totalFrames);

        var watch = Stopwatch.StartNew();
        ScheduleBuildResult schedule = BuildSchedule(
            waveFormat.SampleRate,
            level,
            timingMap,
            timeline,
            clips,
            _totalFrames);
        schedule.Hits.Sort(static (left, right) => left.StartFrame.CompareTo(right.StartFrame));
        watch.Stop();

        _scheduledHits = schedule.Hits;
        _sourceHitCount = schedule.SourceHitCount;
        _buildSchedule = watch.Elapsed;
        _maxClipFrames = _scheduledHits.Count == 0
            ? 0
            : _scheduledHits.Max(static hit => (long)hit.Clip.FrameCount);
        Seek(0);
    }

    public WaveFormat WaveFormat { get; }
    internal long PositionFrames => _positionFrames;

    internal long CachedPcmBytes
    {
        get
        {
            lock (_cacheLock)
                return _cachedPcmBytes;
        }
    }

    internal HitSoundRenderMetrics Metrics
    {
        get
        {
            lock (_cacheLock)
            {
                return new HitSoundRenderMetrics(
                    _buildSchedule,
                    _renderChunksElapsed,
                    _sourceHitCount,
                    _scheduledHits.Count,
                    _renderedChunks.Count);
            }
        }
    }

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

    /// <summary>
    /// Ensures the requested range is already in the RAM cache. AudioPlayer uses
    /// this before WaveOut starts so the first device callback never pays the
    /// initial chunk-render cost.
    /// </summary>
    internal void PrimeRange(long startFrame, long frameCount)
    {
        if (frameCount <= 0 || _totalFrames <= 0)
            return;

        long start = Math.Clamp(startFrame, 0, _totalFrames);
        long remaining = _totalFrames - start;
        long length = Math.Min(frameCount, remaining);
        if (length <= 0)
            return;

        long firstChunk = start / ChunkFrames;
        long lastChunk = (start + length - 1) / ChunkFrames;
        for (long chunkIndex = firstChunk; chunkIndex <= lastChunk; chunkIndex++)
            _ = EnsureChunk(chunkIndex);
    }

    /// <summary>
    /// Renders future chunks as fast as possible without allowing background PCM
    /// additions to push the shared RAM cache past the supplied byte budget.
    /// Already cached chunks cost nothing, and silent sentinels consume no PCM
    /// budget. Foreground Read remains authoritative and may render a cache miss
    /// synchronously even when a background budget has been exhausted.
    /// </summary>
    internal Task PrerenderAheadAsync(
        long startFrame,
        long cacheBudgetBytes,
        CancellationToken cancellationToken)
    {
        if (_totalFrames <= 0 || cacheBudgetBytes <= 0)
            return Task.CompletedTask;

        long start = Math.Clamp(startFrame, 0, _totalFrames);
        if (start >= _totalFrames)
            return Task.CompletedTask;

        long firstChunk = start / ChunkFrames;
        long lastChunk = (_totalFrames - 1) / ChunkFrames;

        return Task.Run(() =>
        {
            for (long chunkIndex = firstChunk; chunkIndex <= lastChunk; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryEnsureChunkWithinBudget(chunkIndex, cacheBudgetBytes))
                    break;
            }
        }, cancellationToken);
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

            // At extreme BPM several adjacent floors often round to the exact
            // same sample. Linear mixing lets those identical contributions
            // collapse into one scaled voice without changing their waveform.
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

    private float[] EnsureChunk(long chunkIndex)
    {
        lock (_cacheLock)
        {
            if (_renderedChunks.TryGetValue(chunkIndex, out float[]? existing))
                return existing;

            float[] rendered = RenderChunkMeasured(chunkIndex);
            _renderedChunks.Add(chunkIndex, rendered);
            _cachedPcmBytes += GetPcmBytes(rendered);
            return rendered;
        }
    }

    private bool TryEnsureChunkWithinBudget(long chunkIndex, long cacheBudgetBytes)
    {
        lock (_cacheLock)
        {
            if (_renderedChunks.ContainsKey(chunkIndex))
                return true;

            float[] rendered = RenderChunkMeasured(chunkIndex);
            long renderedBytes = GetPcmBytes(rendered);
            if (renderedBytes > cacheBudgetBytes - _cachedPcmBytes)
                return false;

            _renderedChunks.Add(chunkIndex, rendered);
            _cachedPcmBytes += renderedBytes;
            return true;
        }
    }

    private float[] RenderChunkMeasured(long chunkIndex)
    {
        var watch = Stopwatch.StartNew();
        float[] rendered = RenderChunk(chunkIndex);
        watch.Stop();
        _renderChunksElapsed += watch.Elapsed;
        return rendered;
    }

    private static long GetPcmBytes(float[] chunk) => checked(chunk.LongLength * sizeof(float));

    private float[] RenderChunk(long chunkIndex)
    {
        if (_scheduledHits.Count == 0 || _totalFrames <= 0 || chunkIndex < 0)
            return [];

        long chunkStart = chunkIndex * ChunkFrames;
        if (chunkStart < 0 || chunkStart >= _totalFrames)
            return [];

        long chunkEnd = Math.Min(_totalFrames, chunkStart + ChunkFrames);
        long earliestRelevantStart = chunkStart - _maxClipFrames + 1;
        int hitIndex = LowerBoundStartFrame(earliestRelevantStart);
        float[]? chunk = null;

        for (; hitIndex < _scheduledHits.Count; hitIndex++)
        {
            ScheduledHit hit = _scheduledHits[hitIndex];
            if (hit.StartFrame >= chunkEnd)
                break;

            long hitEnd = hit.StartFrame + hit.Clip.FrameCount;
            long overlapStart = Math.Max(chunkStart, Math.Max(0, hit.StartFrame));
            long overlapEnd = Math.Min(chunkEnd, hitEnd);
            if (overlapEnd <= overlapStart)
                continue;

            chunk ??= new float[ChunkFrames * 2];
            long sourceFrame = overlapStart - hit.StartFrame;
            int destinationFrame = checked((int)(overlapStart - chunkStart));
            int framesToMix = checked((int)(overlapEnd - overlapStart));
            AddScaled(
                chunk,
                destinationFrame * 2,
                hit.Clip.Samples,
                checked((int)sourceFrame) * 2,
                framesToMix * 2,
                hit.Volume);
        }

        // Cache an empty sentinel too, so repeatedly reading a silent region does
        // not repeat the schedule search.
        return chunk ?? [];
    }

    private int LowerBoundStartFrame(long startFrame)
    {
        int low = 0;
        int high = _scheduledHits.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_scheduledHits[middle].StartFrame < startFrame)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
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

            float[] chunk = frame < _totalFrames ? EnsureChunk(chunkIndex) : [];
            if (chunk.Length > 0)
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
}
