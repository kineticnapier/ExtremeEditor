namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static void RegisterTrackColorEventSchemas()
    {
        if (EventPropertySchemas is not Dictionary<string, EventPropertyDefinition[]> schemas)
            return;

        schemas["ColorTrack"] =
        [
            TextProperty("trackColorType", "Single"),
            TextProperty("trackColor", "debb7b"),
            TextProperty("secondaryTrackColor", "ffffff"),
            NumberProperty("trackColorAnimDuration", 0.0, "beats"),
            TextProperty("trackColorPulse", "None"),
            IntegerProperty("trackPulseLength", 10),
            TextProperty("trackStyle", "Standard"),
            TextProperty("trackTexture", ""),
            NumberProperty("trackTextureScale", 1.0),
            NumberProperty("trackGlowIntensity", 100.0, "%"),
            BoolProperty("floorIconOutlines", false),
            BoolProperty("justThisTile", false)
        ];

        schemas["RecolorTrack"] =
        [
            IntegerProperty("gapLength", 0),
            TextProperty("trackColorType", "Single"),
            TextProperty("trackColor", "debb7b"),
            TextProperty("secondaryTrackColor", "ffffff"),
            NumberProperty("trackColorAnimDuration", 0.0, "beats"),
            TextProperty("trackColorPulse", "None"),
            IntegerProperty("trackPulseLength", 10),
            TextProperty("trackStyle", "Standard"),
            TextProperty("trackTexture", ""),
            NumberProperty("trackTextureScale", 1.0),
            NumberProperty("trackGlowIntensity", 100.0, "%"),
            BoolProperty("floorIconOutlines", false),
            NumberProperty("duration", 0.0, "beats"),
            NumberProperty("angleOffset", 0.0, "°"),
            TextProperty("ease", "Linear"),
            TextProperty("eventTag", "")
        ];
    }
}
