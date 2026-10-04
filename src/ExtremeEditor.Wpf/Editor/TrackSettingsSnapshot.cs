namespace ExtremeEditor.Wpf;

internal sealed record TrackSettingsSnapshot(
    string TrackColorType,
    string TrackColor,
    string SecondaryTrackColor,
    double TrackColorAnimDuration,
    string TrackColorPulse,
    int TrackPulseLength,
    string TrackStyle,
    double TrackGlowIntensity);
