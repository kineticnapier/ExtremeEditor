namespace ExtremeEditor.Wpf;

internal sealed record LevelSettingsSnapshot(
    string? SongFilename,
    double InitialBpm,
    double SongVolumePercent,
    double OffsetMilliseconds,
    double PitchPercent,
    string DefaultHitSound,
    double HitSoundVolumePercent);
