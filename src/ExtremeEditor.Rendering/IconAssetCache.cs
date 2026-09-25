namespace ExtremeEditor.Rendering;

public static class IconAssetCache
{
    public static string IconDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "icons");

    public static string EventDirectory => Path.Combine(IconDirectory, "events");
    public static string FloorDirectory => Path.Combine(IconDirectory, "floors");
    public static string OutlineDirectory => Path.Combine(IconDirectory, "outlines");
    public static string CategoryDirectory => Path.Combine(IconDirectory, "categories");

    public static string EventPath(string eventType) => Path.Combine(EventDirectory, SafeName(eventType) + ".png");
    public static string FloorPath(string floorIcon) => Path.Combine(FloorDirectory, SafeName(floorIcon) + ".png");
    public static string OutlinePath(string floorIcon) => Path.Combine(OutlineDirectory, SafeName(floorIcon) + ".png");
    public static string CategoryPath(string category) => Path.Combine(CategoryDirectory, SafeName(category) + ".png");

    private static string SafeName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
