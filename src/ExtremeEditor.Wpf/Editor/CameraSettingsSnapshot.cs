namespace ExtremeEditor.Wpf;

internal sealed record CameraSettingsSnapshot(
    string RelativeTo,
    double PositionX,
    double PositionY,
    double Rotation,
    double Zoom);
