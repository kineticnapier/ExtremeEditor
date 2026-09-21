namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly string[] TrackColorTypes =
        ["Single", "Stripes", "Glow", "Blink", "Switch", "Rainbow", "Volume"];

    private static readonly string[] TrackColorPulses =
        ["None", "Forward", "Backward"];

    private static readonly string[] TrackStyles =
        ["Standard", "Neon", "NeonLight", "Basic", "Minimal", "Gems"];

    private static void RegisterTrackColorEventSchemas()
    {
        if (EventPropertySchemas is not Dictionary<string, EventPropertyDefinition[]> schemas)
            return;

        schemas["ColorTrack"] =
        [
            EnumProperty("trackColorType", "Single", TrackColorTypes),
            TextProperty("trackColor", "debb7b"),
            TextProperty("secondaryTrackColor", "ffffff"),
            NumberProperty("trackColorAnimDuration", 2.0, "seconds"),
            EnumProperty("trackColorPulse", "None", TrackColorPulses),
            IntegerProperty("trackPulseLength", 10, "tiles"),
            EnumProperty("trackStyle", "Standard", TrackStyles),
            TextProperty("trackTexture", ""),
            NumberProperty("trackTextureScale", 1.0),
            NumberProperty("trackGlowIntensity", 100.0, "%"),
            BoolProperty("floorIconOutlines", false),
            BoolProperty("justThisTile", false)
        ];

        schemas["RecolorTrack"] =
        [
            TileReferenceProperty("startTile", 0, "ThisTile"),
            TileReferenceProperty("endTile", 0, "ThisTile"),
            IntegerProperty("gapLength", 0),
            NumberProperty("duration", 0.0, "beats"),
            EnumProperty("trackColorType", "Single", TrackColorTypes),
            TextProperty("trackColor", "debb7b"),
            TextProperty("secondaryTrackColor", "ffffff"),
            NumberProperty("trackColorAnimDuration", 2.0, "seconds"),
            EnumProperty("trackColorPulse", "None", TrackColorPulses),
            IntegerProperty("trackPulseLength", 10, "tiles"),
            EnumProperty("trackStyle", "Standard", TrackStyles),
            NumberProperty("trackGlowIntensity", 100.0, "%"),
            NumberProperty("angleOffset", 0.0, "°"),
            TextProperty("ease", "Linear"),
            TextProperty("eventTag", "")
        ];
    }

    private static EventPropertyDefinition TileReferenceProperty(string name, int offset, string mode) =>
        new(name, EventPropertyEditorKind.Vector2, new object?[] { offset, mode }, string.Empty, null);
}