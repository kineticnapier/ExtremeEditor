using System.Diagnostics;
using System.IO;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed class PreparedAudioLoad : IDisposable
{
    private PreparedAudioLoad(
        AudioPlayer player,
        string state,
        TimeSpan configureTime,
        TimeSpan loadTime)
    {
        Player = player;
        State = state;
        ConfigureTime = configureTime;
        LoadTime = loadTime;
    }

    internal AudioPlayer Player { get; }
    internal string State { get; }
    internal TimeSpan ConfigureTime { get; }
    internal TimeSpan LoadTime { get; }

    internal static PreparedAudioLoad Prepare(
        LevelDocument level,
        WpfPlaybackSetup playback,
        bool hitSoundsEnabled)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(playback);

        var player = new AudioPlayer
        {
            HitSoundsEnabled = hitSoundsEnabled
        };

        try
        {
            var watch = Stopwatch.StartNew();
            player.ConfigureHitSounds(
                level,
                playback.TimingMap,
                playback.HitSoundTimeline);
            watch.Stop();
            TimeSpan configureTime = watch.Elapsed;

            string state;
            TimeSpan loadTime = TimeSpan.Zero;
            string? songPath = playback.SongPath;
            if (songPath is null)
            {
                state = "audio unavailable";
            }
            else if (!File.Exists(songPath))
            {
                state = $"audio missing: {Path.GetFileName(songPath)}";
            }
            else
            {
                watch.Restart();
                try
                {
                    player.Load(songPath);
                    state = $"audio {Path.GetFileName(songPath)}";
                }
                catch (Exception ex)
                {
                    player.Unload();
                    state = $"audio load failed: {ex.Message}";
                }
                finally
                {
                    watch.Stop();
                    loadTime = watch.Elapsed;
                }
            }

            return new PreparedAudioLoad(player, state, configureTime, loadTime);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    public void Dispose() => Player.Dispose();
}
