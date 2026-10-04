namespace ExtremeEditor.Wpf;

internal sealed record TrackSettingsSnapshot
{
    internal TrackSettingsSnapshot(
        string trackColorType, string trackColor, string secondaryTrackColor,
        double trackColorAnimDuration, string trackColorPulse, int trackPulseLength,
        string trackStyle, double trackGlowIntensity)
        : this(
            trackColorType, trackColor, secondaryTrackColor, trackColorAnimDuration,
            trackColorPulse, trackPulseLength, trackStyle, trackGlowIntensity,
            "None", 3.0, "None", 4.0)
    {
        HasTrackAnimationSettings = false;
    }

    internal TrackSettingsSnapshot(
        string trackColorType, string trackColor, string secondaryTrackColor,
        double trackColorAnimDuration, string trackColorPulse, int trackPulseLength,
        string trackStyle, double trackGlowIntensity, string trackAnimation,
        double beatsAhead, string trackDisappearAnimation, double beatsBehind)
    {
        TrackColorType = trackColorType;
        TrackColor = trackColor;
        SecondaryTrackColor = secondaryTrackColor;
        TrackColorAnimDuration = trackColorAnimDuration;
        TrackColorPulse = trackColorPulse;
        TrackPulseLength = trackPulseLength;
        TrackStyle = trackStyle;
        TrackGlowIntensity = trackGlowIntensity;
        TrackAnimation = trackAnimation;
        BeatsAhead = beatsAhead;
        TrackDisappearAnimation = trackDisappearAnimation;
        BeatsBehind = beatsBehind;
        HasTrackAnimationSettings = true;
    }

    internal string TrackColorType { get; init; }
    internal string TrackColor { get; init; }
    internal string SecondaryTrackColor { get; init; }
    internal double TrackColorAnimDuration { get; init; }
    internal string TrackColorPulse { get; init; }
    internal int TrackPulseLength { get; init; }
    internal string TrackStyle { get; init; }
    internal double TrackGlowIntensity { get; init; }
    internal string TrackAnimation { get; init; }
    internal double BeatsAhead { get; init; }
    internal string TrackDisappearAnimation { get; init; }
    internal double BeatsBehind { get; init; }
    internal bool HasTrackAnimationSettings { get; init; }
}
