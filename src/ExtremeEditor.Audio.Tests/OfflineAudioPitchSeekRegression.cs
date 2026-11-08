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

            // Compare partial exports against the same full export at several
            // playback rates and frame boundaries. Some start frames map to
            // fractional source positions, others land exactly on a source frame.
            foreach (double pitch in new[] { 75.0, 100.0, 115.0, 133.0 })
            {
                level.PitchPercent = pitch;
                float[] whole = Render(0.0);
                if (whole.Length != 960 * 2)
                    throw new InvalidOperationException(
                        $"Pitch {pitch}% changed the full offline output length.");

                foreach (double startSeconds in new[] { 0.0005, 0.001, 0.0010625, 0.0017 })
                {
                    int startFrame = checked((int)Math.Round(
                        startSeconds * sampleRate, MidpointRounding.AwayFromZero));
                    int startSamples = startFrame * 2;
                    float[] seeked = Render(startSeconds);
                    if (seeked.Length != whole.Length - startSamples)
                        throw new InvalidOperationException(
                            $"Pitch {pitch}%, start frame {startFrame}: " +
                            "partial offline output length differs.");

                    for (int i = 0; i < seeked.Length; i++)
                    {
                        if (Math.Abs(seeked[i] - whole[startSamples + i]) > 0.0002f)
                        {
                            throw new InvalidOperationException(
                                $"Pitch {pitch}%, start frame {startFrame}, sample {i}: " +
                                $"full={whole[startSamples + i]} seek={seeked[i]}.");
                        }
                    }
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
