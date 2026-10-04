namespace ExtremeEditor.Wpf;

internal sealed record MiscSettingsSnapshot
{
    public MiscSettingsSnapshot(bool stickToFloors)
    {
        StickToFloors = stickToFloors;
        HasFloorIconOutlines = false;
    }

    public MiscSettingsSnapshot(bool stickToFloors, bool floorIconOutlines)
    {
        StickToFloors = stickToFloors;
        FloorIconOutlines = floorIconOutlines;
        HasFloorIconOutlines = true;
    }

    public bool StickToFloors { get; init; }
    public bool FloorIconOutlines { get; init; }
    internal bool HasFloorIconOutlines { get; init; }
}
