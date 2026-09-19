using System.Numerics;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Serves a prerendered hit-sound PCM layer on the same absolute sample clock as
/// the song. Floor/timing/timeline/name lookup and overlapping-voice mixing are
/// completed in the constructor so the realtime callback only copies PCM chunks.
/// </summary>
internal sealed class SampleAccurateHitSoundProvider : ISampleProvider
{
    private const int ChunkFrames = 4096;

    private readonly ScheduledHit[] _scheduledHits;
    private readonly Dictionary<long, float[]> _renderedChunks;
    private long _positionFrames;

    public SampleAccurateHitSoundProvider(
        WaveFormat waveFormat,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat || waveFormat.Channels != 2)
            throw new ArgumentException("The hit-sound renderer requires stereo IEEE float audio.", nameof(waveFormat));

        WaveFormat = waveFormat;
        _scheduledHits = BuildSchedule(waveFormat.SampleRate, level, timingMap, timeline, clips);
        _renderedChunks = RenderChunks(_scheduledHits);
        Seek(0);
    }

    public WaveFormat WaveFormat { get; }
    internal long PositionFrames => _positionFrames;

    public int Read(float[] buffer, int offset, int count)
    {
        int frameCount = count / WaveFormat.Channels;
        int sampleCount = frameCount * WaveFormat.Channels;
        Array.Clear(buffer, offset, sampleCount);
        if (frameCount == 0)
            return 0;

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

    private static ScheduledHit[] BuildSchedule(
        int sampleRate,
        LevelDocument level,
        TimingMap timingMap,
        HitSoundTimeline timeline,
        IReadOnlyDictionary<string, RenderedHitSound> clips)
    {
        if (clips.Count == 0)
            return [];

        int floorCount = Math.Min(level.FloorCount, timingMap.Floors.Count);
        if (floorCount <= 1)
            return [];

        var hits = new List<ScheduledHit>(floorCount - 1);
        for (int floor = 1; floor < floorCount; floor++)
        {
            if (timingMap.Floors[floor].MidSpin)
                continue;

            HitSoundState state = timeline.GetStateAtFloor(floor);
            if (string.Equals(state.Name, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            string name = HitSoundLibrary.NormalizeName(state.Name);
            if (!clips.TryGetValue(name, out RenderedHitSound? clip))
                continue;

            double floorAudio = PlaybackClock.ChartToAudioTime(level, timingMap.GetEntryTime(floor));
            double startAudio = floorAudio - clip.OffsetSeconds;
            long startFrame = AudioTimeToSampleFrame(startAudio, sampleRate);
            float volume = (float)Math.Clamp(state.Volume, 0.0, 1.0);
            hits.Add(new ScheduledHit(clip, startFrame, volume, floor));
        }

        hits.Sort(static (left, right) =>
        {
            int startOrder = left.StartFrame.CompareTo(right.StartFrame);
            return startOrder != 0 ? startOrder : left.Floor.CompareTo(right.Floor);
        });
        return hits.ToArray();
    }

    private static Dictionary<long, float[]> RenderChunks(ScheduledHit[] hits)
    {
        var chunks = new Dictionary<long, float[]>();
        foreach (ScheduledHit hit in hits)
            RenderHitIntoChunks(chunks, hit);
        return chunks;
    }

    private static void RenderHitIntoChunks(Dictionary<long, float[]> chunks, ScheduledHit hit)
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

            long chunkIndex = destinationFrame / ChunkFrames;
            int frameInChunk = (int)(destinationFrame % ChunkFrames);
            int availableInChunk = ChunkFrames - frameInChunk;
            int framesToMix = (int)Math.Min(availableInChunk, hit.Clip.FrameCount - sourceFrame);

            if (!chunks.TryGetValue(chunkIndex, out float[]? chunk))
            {
                chunk = new float[ChunkFrames * 2];
                chunks.Add(chunkIndex, chunk);
            }

            int sourceSample = checked((int)sourceFrame) * 2;
            int destinationSample = frameInChunk * 2;
            int sampleCount = framesToMix * 2;
            AddScaled(
                chunk,
                destinationSample,
                hit.Clip.Samples,
                sourceSample,
                sampleCount,
                hit.Volume);

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

    private readonly record struct ScheduledHit(
        RenderedHitSound Clip,
        long StartFrame,
        float Volume,
        int Floor);
}
