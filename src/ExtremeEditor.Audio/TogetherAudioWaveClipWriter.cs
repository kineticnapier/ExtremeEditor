using System.Buffers.Binary;
using ExtremeEditor.Core;
using NAudio.Vorbis;
using NAudio.Wave;

namespace ExtremeEditor.Audio;

/// <summary>
/// Streams the decoded source exactly once, writing one PCM16 WAV per assigned
/// section. No imprecise MP3/Vorbis timestamp seeks or external FFmpeg required.
/// </summary>
public sealed class TogetherAudioWaveClipWriter : ITogetherAudioClipWriter
{
    private const int BufferFrames = 4096;

    public void WriteClips(
        string sourceAudioPath,
        int sampleRate,
        IReadOnlyList<TogetherAudioClipRequest> clips)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioPath);
        ArgumentNullException.ThrowIfNull(clips);
        if (sampleRate <= 0 || clips.Count == 0)
            throw new ArgumentException("Expected sample rate and audio cuts are required.");

        using WaveStream reader = string.Equals(
            Path.GetExtension(sourceAudioPath), ".ogg",
            StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(sourceAudioPath)
            : new AudioFileReader(sourceAudioPath);
        ISampleProvider source = reader.ToSampleProvider();
        int channels = source.WaveFormat.Channels;
        if (source.WaveFormat.SampleRate != sampleRate ||
            channels is < 1 or > 8)
            throw new InvalidDataException(
                "Decoded audio format does not match the measured project audio.");

        float[] samples = new float[checked(BufferFrames * channels)];
        byte[] pcm = new byte[checked(samples.Length * sizeof(short))];
        long position = 0;

        foreach (TogetherAudioClipRequest clip in clips)
        {
            if (clip.StartSample < position ||
                clip.EndSampleExclusive <= clip.StartSample)
                throw new ArgumentException(
                    "Audio cut ranges must be increasing, nonempty and nonoverlapping.");

            while (position < clip.StartSample)
            {
                int wantedFrames = checked((int)Math.Min(
                    BufferFrames, clip.StartSample - position));
                int frames = ReadFrames(source, samples, channels, wantedFrames);
                if (frames == 0)
                    throw new InvalidDataException(
                        "Decoded source ended before the requested cut start.");
                position += frames;
            }

            string destination = Path.GetFullPath(clip.DestinationPath);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Audio clip already exists: " + destination);

            var format = new WaveFormat(sampleRate, 16, channels);
            using (var output = new WaveFileWriter(destination, format))
            {
                while (position < clip.EndSampleExclusive)
                {
                    int wantedFrames = checked((int)Math.Min(
                        BufferFrames, clip.EndSampleExclusive - position));
                    int frames = ReadFrames(source, samples, channels, wantedFrames);
                    if (frames == 0)
                        throw new InvalidDataException(
                            "Decoded source ended before the requested cut end.");
                    int count = checked(frames * channels);
                    for (int i = 0; i < count; i++)
                    {
                        float v = samples[i];
                        if (!float.IsFinite(v))
                            throw new InvalidDataException(
                                "Decoded audio contains nonfinite samples.");
                        int quantized = (int)MathF.Round(
                            Math.Clamp(v, -1f, 1f) * 32767f);
                        BinaryPrimitives.WriteInt16LittleEndian(
                            pcm.AsSpan(i * sizeof(short), sizeof(short)),
                            checked((short)quantized));
                    }
                    output.Write(pcm, 0, checked(count * sizeof(short)));
                    position += frames;
                }
            }

            // WAV duration is exactly (end-start)/rate, not rounded to an
            // MP3 granule or codec block boundary.
            using var check = new WaveFileReader(destination);
            long actualFrames = check.Length / check.WaveFormat.BlockAlign;
            if (actualFrames != clip.EndSampleExclusive - clip.StartSample ||
                check.WaveFormat.SampleRate != sampleRate ||
                check.WaveFormat.Channels != channels ||
                check.WaveFormat.BitsPerSample != 16)
                throw new InvalidDataException(
                    "Rendered audio length/format did not survive WAV readback.");
        }

        // The planned length came from full sequential waveform decoding.
        // Reject changed or truncated source instead of publishing misaligned
        // independently editable packages.
        if (ReadFrames(source, samples, channels, 1) != 0)
            throw new InvalidDataException(
                "Decoded source is longer than the planned collaboration timeline.");
    }

    private static int ReadFrames(
        ISampleProvider source, float[] buffer, int channels, int requestedFrames)
    {
        int count = source.Read(buffer, 0, checked(requestedFrames * channels));
        if (count < 0 || count % channels != 0)
            throw new InvalidDataException("Audio decoder returned incomplete frames.");
        return count / channels;
    }
}
