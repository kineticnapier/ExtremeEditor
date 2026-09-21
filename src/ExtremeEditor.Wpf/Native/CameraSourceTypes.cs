namespace ExtremeEditor.Wpf.Native;

internal readonly record struct CameraSourcePosition(double? X, double? Y);

internal sealed record CameraSourceEvent(
    int SourceIndex,
    int Floor,
    bool Active,
    double Duration,
    string RelativeTo,
    CameraSourcePosition Position,
    double? Rotation,
    double? Zoom,
    double AngleOffset,
    string Ease);
