using System.IO;
using System.Runtime.CompilerServices;

namespace ExtremeEditor.Audio.Tests;

internal static class AudioPlayerPrerenderWiringRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        string source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.Audio", "AudioPlayer.cs"));

        if (!source.Contains("_hitSoundPrerender.Start(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AudioPlayer.BuildGraph creates the progressive hit-sound PCM provider but never starts its prerender lifecycle, so the realtime WaveOut callback still renders cache misses synchronously.");
        }

        if (!source.Contains("AudioPcmCachePolicy.GetAvailableMemoryBytes()", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AudioPlayer must initialize hit-sound prerendering with the runtime memory budget policy.");
        }
    }
}
