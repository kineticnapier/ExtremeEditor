using System.Reflection;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class MutedPrerenderRegression
{
    internal static void Run()
    {
        // Exercise the lifecycle without opening a WaveOut device. This checks
        // that OFF cancels speculative rendering, Seek while OFF cannot restart
        // it, and ON reattaches the same PCM provider.
        using var player = new AudioPlayer(loadHitSoundAssets: false);
        player.HitSoundsEnabled = true;

        LevelDocument level = LevelDocument.CreateSynthetic(2);
        TimingMap timing = TimingMapBuilder.Build(level);
        HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var provider = new SampleAccurateHitSoundProvider(
            format, level, timing, timeline,
            new Dictionary<string, RenderedHitSound>(), totalFrames: 48_000);

        SetField(player, "_hitSoundProvider", provider);
        SetField(player, "_outputFormat", format);
        var lifecycle = (AudioPcmPrerenderLifecycle)GetField(player, "_hitSoundPrerender")!;
        lifecycle.Start(provider, format.SampleRate, 0, availableMemoryBytes: 0);
        if (!ReferenceEquals(GetField(lifecycle, "_provider"), provider))
            throw new InvalidOperationException("Prerender test setup did not attach the PCM provider.");

        player.HitSoundsEnabled = false;
        if (GetField(lifecycle, "_provider") is not null)
            throw new InvalidOperationException("Muting hitsounds must cancel speculative PCM prerender.");

        MethodInfo restart = typeof(AudioPlayer).GetMethod(
            "RestartHitSoundPrerender", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RestartHitSoundPrerender was not found.");
        restart.Invoke(player, [500L]);
        if (GetField(lifecycle, "_provider") is not null)
            throw new InvalidOperationException("Seek while muted must not restart PCM prerender.");

        player.HitSoundsEnabled = true;
        if (!ReferenceEquals(GetField(lifecycle, "_provider"), provider))
            throw new InvalidOperationException("Unmuting must resume prerender with the current PCM provider.");
    }

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field: {name}");
        field.SetValue(instance, value);
    }
}
