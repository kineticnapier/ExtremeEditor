using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed partial class EditorSession
{
    public CameraSettingsSnapshot GetCameraSettings()
    {
        LevelCameraSettings camera = Document.CameraSettings;
        return new CameraSettingsSnapshot(
            camera.RelativeTo,
            camera.PositionX,
            camera.PositionY,
            camera.Rotation,
            camera.Zoom);
    }

    public void EditCameraSettings(CameraSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CameraSettingsSnapshot normalized = NormalizeCameraSettings(settings);
        CameraSettingsSnapshot before = GetCameraSettings();
        if (before == normalized)
            return;
        Execute(new EditCameraSettingsCommand(before, normalized));
    }

    internal void ApplyCameraSettingsRaw(CameraSettingsSnapshot settings)
    {
        Document.CameraSettings = new LevelCameraSettings(
            settings.RelativeTo,
            settings.PositionX,
            settings.PositionY,
            settings.Rotation,
            settings.Zoom);

        JsonObject root = EnsureSourceRoot();
        JsonObject sourceSettings = root["settings"] as JsonObject ?? new JsonObject();
        sourceSettings["relativeTo"] = settings.RelativeTo;
        sourceSettings["position"] = new JsonArray(settings.PositionX, settings.PositionY);
        sourceSettings["rotation"] = settings.Rotation;
        sourceSettings["zoom"] = settings.Zoom;
        root["settings"] = sourceSettings;
    }

    private static CameraSettingsSnapshot NormalizeCameraSettings(CameraSettingsSnapshot settings)
    {
        static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        string relativeTo = string.IsNullOrWhiteSpace(settings.RelativeTo)
            ? "Player"
            : settings.RelativeTo.Trim();
        return settings with
        {
            RelativeTo = relativeTo,
            PositionX = FiniteOr(settings.PositionX, 0.0),
            PositionY = FiniteOr(settings.PositionY, 0.0),
            Rotation = FiniteOr(settings.Rotation, 0.0),
            Zoom = Math.Max(0.000001, FiniteOr(settings.Zoom, 100.0))
        };
    }

    private sealed class EditCameraSettingsCommand(
        CameraSettingsSnapshot before,
        CameraSettingsSnapshot after) : IEditorCommand
    {
        public string Name => "Edit camera settings";
        public void Execute(EditorSession session) => session.ApplyCameraSettingsRaw(after);
        public void Undo(EditorSession session) => session.ApplyCameraSettingsRaw(before);
    }
}
