using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

namespace ExtremeEditor.Audio.Tests;

internal static class TransportIdentityPrerenderRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        FieldInfo? sourcePcm = typeof(TransportHitSoundSampleProvider).GetField(
            "_sourcePcm",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (sourcePcm is null || sourcePcm.FieldType != typeof(SampleAccurateHitSoundProvider))
        {
            throw new InvalidOperationException(
                "Dense 1x playback must retain the prerendered SampleAccurateHitSoundProvider instead of remixing every active hit voice per output frame.");
        }

        MethodInfo? identityRead = typeof(TransportHitSoundSampleProvider).GetMethod(
            "ReadIdentitySourcePcm",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (identityRead is null)
        {
            throw new InvalidOperationException(
                "TransportHitSoundSampleProvider needs a block PCM fast path for identity (1x) playback.");
        }


        VerifyIdentityPathPreservesPostSongTail();
        VerifyHitSoundLibraryIsShared();
    }

    private static void VerifyIdentityPathPreservesPostSongTail()
    {
        const int sampleRate = 1000;
        LevelDocument level = LevelDocument.CreateSynthetic(2);
        level.SeparateCountdownTime = false;
        level.CountdownTicks = 0;
        var timing = new TimingMap([
            new FloorTiming(0, 0, 0, 0, 0, 0, 0, false, false),
            new FloorTiming(1, 0.095, 0.095, 0, 0, 0, 0, false, false)]);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        var clips = new Dictionary<string, RenderedHitSound>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kick"] = new RenderedHitSound(Enumerable.Repeat(0.25f, 40).ToArray(), 0)
        };
        var pcm = new SampleAccurateHitSoundProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            level, timing, timeline, clips, totalFrames: 100);
        var transport = new TransportHitSoundSampleProvider(pcm, 1.0);
        var song = new float[200];
        if (transport.Read(song, 0, song.Length) != song.Length)
            throw new InvalidOperationException("#38 fixture did not consume the identity song range.");
        var tail = new float[40];
        int read = transport.Read(tail, 0, tail.Length);
        if (read <= 0 || tail.All(sample => Math.Abs(sample) < 0.000001f))
            throw new InvalidOperationException("#38: identity playback discarded the hit-sound tail after song EOF.");
    }

    private static void VerifyHitSoundLibraryIsShared()
    {
        using var first = new AudioPlayer();
        using var second = new AudioPlayer();
        FieldInfo field = typeof(AudioPlayer).GetField(
            "_hitSoundLibrary", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (!ReferenceEquals(field.GetValue(first), field.GetValue(second)))
            throw new InvalidOperationException("#49: AudioPlayer instances duplicate decoded hit-sound assets.");
    }
}
