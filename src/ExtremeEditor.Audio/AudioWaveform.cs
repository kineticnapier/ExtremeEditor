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
        int maximumPeakCount = 16_384) =>
        throw new NotImplementedException(
            "RED: decode a bounded waveform summary from the real audio source.");
}
