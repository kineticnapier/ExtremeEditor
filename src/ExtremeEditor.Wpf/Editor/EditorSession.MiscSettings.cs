using System.Text.Json.Nodes;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed partial class EditorSession
{
    public MiscSettingsSnapshot GetMiscSettings() => new(
        TrackTransformMetadataCache.Get(Document).DefaultStickToFloors,
        TrackVisualMetadataCache.Get(Document).InitialStyle.FloorIconOutlines);

    public void EditMiscSettings(MiscSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MiscSettingsSnapshot before = GetMiscSettings();
        MiscSettingsSnapshot normalized = settings.HasFloorIconOutlines
            ? settings
            : new MiscSettingsSnapshot(settings.StickToFloors, before.FloorIconOutlines);
        if (before == normalized)
            return;
        Execute(new EditMiscSettingsCommand(before, normalized));
    }

    internal void ApplyMiscSettingsRaw(MiscSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        TrackTransformSourceData current = TrackTransformMetadataCache.Get(Document);
        TrackTransformMetadataCache.Attach(
            Document,
            current with { DefaultStickToFloors = settings.StickToFloors });

        TrackVisualSourceData visual = TrackVisualMetadataCache.Get(Document);
        TrackVisualMetadataCache.Attach(
            Document,
            visual with
            {
                InitialStyle = visual.InitialStyle with
                {
                    FloorIconOutlines = settings.FloorIconOutlines
                }
            });

        JsonObject root = EnsureSourceRoot();
        JsonObject sourceSettings = root["settings"] as JsonObject ?? new JsonObject();
        sourceSettings["stickToFloors"] = settings.StickToFloors;
        sourceSettings["floorIconOutlines"] = settings.FloorIconOutlines;
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
