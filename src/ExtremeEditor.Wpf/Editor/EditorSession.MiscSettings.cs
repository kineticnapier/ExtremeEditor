using System.Text.Json.Nodes;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed partial class EditorSession
{
    public MiscSettingsSnapshot GetMiscSettings() =>
        new(TrackTransformMetadataCache.Get(Document).DefaultStickToFloors);

    public void EditMiscSettings(MiscSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MiscSettingsSnapshot before = GetMiscSettings();
        if (before == settings)
            return;
        Execute(new EditMiscSettingsCommand(before, settings));
    }

    internal void ApplyMiscSettingsRaw(MiscSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        TrackTransformSourceData current = TrackTransformMetadataCache.Get(Document);
        TrackTransformMetadataCache.Attach(
            Document,
            current with { DefaultStickToFloors = settings.StickToFloors });

        JsonObject root = EnsureSourceRoot();
        JsonObject sourceSettings = root["settings"] as JsonObject ?? new JsonObject();
        sourceSettings["stickToFloors"] = settings.StickToFloors;
        root["settings"] = sourceSettings;
    }

    private sealed class EditMiscSettingsCommand(
        MiscSettingsSnapshot before,
        MiscSettingsSnapshot after) : IEditorCommand
    {
        public string Name => "Edit misc settings";
        public void Execute(EditorSession session) => session.ApplyMiscSettingsRaw(after);
        public void Undo(EditorSession session) => session.ApplyMiscSettingsRaw(before);
    }
}
