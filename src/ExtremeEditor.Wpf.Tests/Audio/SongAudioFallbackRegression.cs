using System;
using System.IO;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SongAudioFallbackRegression
{
    internal static void Run()
    {
        int songCalls = 0;
        int fallbackCalls = 0;
        int unloadCalls = 0;

        string success = SongAudioFallback.Load(
            "working.ogg",
            _ => songCalls++,
            () => fallbackCalls++,
            () => unloadCalls++);
        Require(success == "audio working.ogg", "normal song status");
        Require(songCalls == 1 && fallbackCalls == 0 && unloadCalls == 0,
            "valid song must not build a fallback or unload");

        string recovered = SongAudioFallback.Load(
            "broken.ogg",
            _ => { songCalls++; throw new InvalidDataException("invalid stream"); },
            () => fallbackCalls++,
            () => unloadCalls++);
        Require(recovered.Contains("invalid stream", StringComparison.Ordinal) &&
                recovered.Contains("hitsounds only", StringComparison.Ordinal),
            "fallback status must preserve the original decoder error");
        Require(songCalls == 2 && fallbackCalls == 1 && unloadCalls == 0,
            "decoder failure must build hitsound-only playback without unloading it");

        string failed = SongAudioFallback.Load(
            "broken.ogg",
            _ => { songCalls++; throw new InvalidDataException("bad decoder"); },
            () => { fallbackCalls++; throw new InvalidOperationException("no device"); },
            () => unloadCalls++);
        Require(failed.Contains("bad decoder", StringComparison.Ordinal) &&
                failed.Contains("no device", StringComparison.Ordinal),
            "both failures must remain visible");
        Require(songCalls == 3 && fallbackCalls == 2 && unloadCalls == 1,
            "fallback failure must release the partial playback graph");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
