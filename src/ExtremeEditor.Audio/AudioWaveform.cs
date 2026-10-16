using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

public sealed record AudioWaveformPeak(
    long StartSample,
    long EndSampleExclusive,
    float Minimum,
    float Maximum);

public sealed record AudioWaveformData(
    int SampleRate,
    long TotalSamples,
    IReadOnlyList<AudioWaveformPeak> Peaks);

public static class AudioWaveformLoader
{
    public static AudioWaveformData Load(
        string path,
        int maximumPeakCount = 16_384)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The waveform audio source does not exist.", path);
        if (maximumPeakCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPeakCount),
                "The waveform peak budget must be positive.");

        using WaveStream reader = OpenReader(path);
        ISampleProvider provider = reader.ToSampleProvider();
        int sampleRate = provider.WaveFormat.SampleRate;
        int channels = provider.WaveFormat.Channels;
        if (sampleRate <= 0 || channels <= 0)
            throw new InvalidDataException(
                "The audio decoder returned an invalid sample format.");

        long estimatedFrames = reader.WaveFormat.BlockAlign > 0
            ? reader.Length / reader.WaveFormat.BlockAlign
            : checked((long)Math.Ceiling(
                reader.TotalTime.TotalSeconds * sampleRate));
        if (estimatedFrames <= 0)
            throw new InvalidDataException(
                "The audio source contains no decodable sample frames.");
        long framesPerPeak = Math.Max(1,
            DivideCeiling(estimatedFrames, maximumPeakCount));

        var peaks = new List<AudioWaveformPeak>(maximumPeakCount);
        var buffer = new float[checked(4_096 * channels)];
        long frameIndex = 0;
        long bucketStart = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        while (true)
        {
            int read = provider.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            int frames = read / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                int sampleIndex = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = Math.Clamp(
                        buffer[sampleIndex + channel], -1f, 1f);
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }

                frameIndex++;
                if (frameIndex - bucketStart >= framesPerPeak)
                {
                    peaks.Add(new AudioWaveformPeak(
                        bucketStart, frameIndex, minimum, maximum));
                    bucketStart = frameIndex;
                    minimum = float.PositiveInfinity;
                    maximum = float.NegativeInfinity;
                }
            }
        }

        if (frameIndex > bucketStart)
            peaks.Add(new AudioWaveformPeak(
                bucketStart, frameIndex, minimum, maximum));
        if (frameIndex == 0 || peaks.Count == 0)
            throw new InvalidDataException(
                "The audio source contains no decodable sample frames.");

        AudioWaveformPeak[] bounded = peaks.Count <= maximumPeakCount
            ? peaks.ToArray()
            : Coalesce(peaks, maximumPeakCount);
        return new AudioWaveformData(
            sampleRate,
            frameIndex,
            Array.AsReadOnly(bounded));
    }

    private static WaveStream OpenReader(string path) =>
        string.Equals(Path.GetExtension(path), ".ogg",
            StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

    private static AudioWaveformPeak[] Coalesce(
        IReadOnlyList<AudioWaveformPeak> source,
        int maximumPeakCount)
    {
        var result = new AudioWaveformPeak[maximumPeakCount];
        for (int output = 0; output < result.Length; output++)
        {
            int first = checked((int)((long)output * source.Count /
                maximumPeakCount));
            int end = checked((int)((long)(output + 1) * source.Count /
                maximumPeakCount));
            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;
            for (int i = first; i < end; i++)
            {
                minimum = Math.Min(minimum, source[i].Minimum);
                maximum = Math.Max(maximum, source[i].Maximum);
            }
            result[output] = new AudioWaveformPeak(
                source[first].StartSample,
                source[end - 1].EndSampleExclusive,
                minimum,
                maximum);
        }
        return result;
    }

    private static long DivideCeiling(long numerator, int denominator) =>
        checked(numerator / denominator +
            (numerator % denominator == 0 ? 0 : 1));
}
