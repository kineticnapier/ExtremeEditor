namespace ExtremeEditor.Wpf;

internal sealed record LevelMetadataSettingsSnapshot(
    string? Song,
    string? Artist,
    string? Author,
    string? LevelDesc,
    string? LevelTags,
    string? ArtistLinks);
