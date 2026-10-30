using System;
using System.IO;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Keeps a playable hit-sound timeline when an existing song cannot be decoded.
/// Song errors remain visible to the caller even when fallback succeeds.
/// </summary>
internal static class SongAudioFallback
{
    internal static string Load(AudioPlayer player, string path)
    {
        ArgumentNullException.ThrowIfNull(player);
        return Load(path, player.Load, player.LoadHitSoundsOnly, player.Unload);
    }

    // Delegate overload makes error recovery testable without a Windows audio device.
    internal static string Load(
        string path,
        Action<string> loadSong,
        Action loadHitSoundsOnly,
        Action unload)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(loadSong);
        ArgumentNullException.ThrowIfNull(loadHitSoundsOnly);
        ArgumentNullException.ThrowIfNull(unload);

        try
        {
            loadSong(path);
            return $"audio {Path.GetFileName(path)}";
        }
        catch (Exception songError)
        {
            try
            {
                // AudioPlayer.Load has already disposed the failed song graph.
                // Reuse the configured level/timing/hit-sound assets with a silent bus.
                loadHitSoundsOnly();
                return $"audio load failed: {songError.Message} (hitsounds only)";
            }
            catch (Exception fallbackError)
            {
                string status = $"audio load failed: {songError.Message}; hitsounds fallback failed: {fallbackError.Message}";
                try
                {
                    unload();
                }
                catch (Exception cleanupError)
                {
                    // Preserve the decoder and fallback errors even if cleanup fails.
                    status += $"; audio cleanup failed: {cleanupError.Message}";
                }

                return status;
            }
        }
    }
}
