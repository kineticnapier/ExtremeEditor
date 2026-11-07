using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed partial class EditorSession
{
    public LevelMetadataSettingsSnapshot GetLevelMetadataSettings()
    {
        LevelMetadata metadata = Document.Metadata;
        return new LevelMetadataSettingsSnapshot(
            metadata.Song,
            metadata.Artist,
            metadata.Author,
            metadata.LevelDescription,
            metadata.LevelTags,
            metadata.ArtistLinks);
    }

    public void EditLevelMetadataSettings(LevelMetadataSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LevelMetadataSettingsSnapshot before = GetLevelMetadataSettings();
        if (before == settings)
            return;
        Execute(new EditLevelMetadataSettingsCommand(before, settings));
    }

    internal void ApplyLevelMetadataSettingsRaw(LevelMetadataSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject sourceSettings = GetSourceSettings();
        SetOptionalString(sourceSettings, "song", settings.Song);
        SetOptionalString(sourceSettings, "artist", settings.Artist);
        SetOptionalString(sourceSettings, "author", settings.Author);
        SetOptionalString(sourceSettings, "levelDesc", settings.LevelDesc);
        SetOptionalString(sourceSettings, "levelTags", settings.LevelTags);
        SetOptionalString(sourceSettings, "artistLinks", settings.ArtistLinks);
        Document.Metadata = new LevelMetadata(
            settings.Song,
            settings.Artist,
            settings.Author,
            settings.LevelDesc,
            settings.LevelTags,
            settings.ArtistLinks);
    }

    private JsonObject GetSourceSettings()
    {
        JsonObject root = EnsureSourceRoot();
        if (root["settings"] is JsonObject settings)
            return settings;
        settings = new JsonObject();
        root["settings"] = settings;
        return settings;
    }

    private static void SetOptionalString(JsonObject settings, string key, string? value)
    {
        if (value is null)
            settings.Remove(key);
        else
            settings[key] = value;
    }

    private sealed class EditLevelMetadataSettingsCommand(
        LevelMetadataSettingsSnapshot before,
        LevelMetadataSettingsSnapshot after) : IEditorCommand
    {
        public string Name => "Edit level metadata settings";
        public void Execute(EditorSession session) => session.ApplyLevelMetadataSettingsRaw(after);
        public void Undo(EditorSession session) => session.ApplyLevelMetadataSettingsRaw(before);
    }
}
