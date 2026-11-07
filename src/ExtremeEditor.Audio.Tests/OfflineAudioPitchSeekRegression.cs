using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class OfflineAudioPitchSeekRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string songPath = Path.Combine(
            Path.GetTempPath(), $"extreme-editor-pitch-seek-{Guid.NewGuid():N}.wav");
        try
        {
            const int sampleRate = 48_000;
            const int sourceFrames = 2_048;
            float[] source = new float[sourceFrames * 2];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                float value = (float)(Math.Sin(frame * 0.031) * 0.4);
                source[frame * 2] = value;
                source[frame * 2 + 1] = value * 0.5f;
            }

            using (var writer = new WaveFileWriter(
                songPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
                writer.WriteSamples(source, 0, source.Length);

            LevelDocument level = LevelDocument.CreateSynthetic(2);
            level.PitchPercent = 115.0;
            TimingMap timing = TimingMapBuilder.Build(level);
            HitSoundTimeline hits = HitSoundTimelineBuilder.Build(level);

            float[] whole = Render(0.0);
            float[] seeked = Render(0.001); // 48 output frames -> 55.2 source frames.
            const int startSamples = 48 * 2;
            if (whole.Length != 960 * 2 ||
                seeked.Length != whole.Length - startSamples)
            {
                throw new InvalidOperationException(
                    "Pitch-adjusted offline rendering changed its output frame count.");
            }

            for (int i = 0; i < seeked.Length; i++)
            {
                if (Math.Abs(seeked[i] - whole[startSamples + i]) > 0.0002f)
                {
                    throw new InvalidOperationException(
                        $"Pitch-adjusted offline seek differs at sample {i}: " +
                        $"full={whole[startSamples + i]} seek={seeked[i]}.");
                }
            }

            float[] Render(double startSeconds)
            {
                var renderer = new OfflineAudioRenderer();
                var plan = new OfflineAudioRenderPlan(
                    level, timing, hits, songPath,
                    StartAudioTimeSeconds: startSeconds,
                    EndAudioTimeSeconds: 0.02,
                    HitSoundsEnabled: false,
                    ChunkFrames: 37);
                var samples = new List<float>();
                renderer.RenderAsync(plan, (chunk, _) =>
                {
                    samples.AddRange(chunk.Samples);
                    return ValueTask.CompletedTask;
                }, CancellationToken.None).GetAwaiter().GetResult();
                return samples.ToArray();
            }
        }
        finally
        {
            if (File.Exists(songPath))
                File.Delete(songPath);
        }
    }
}
