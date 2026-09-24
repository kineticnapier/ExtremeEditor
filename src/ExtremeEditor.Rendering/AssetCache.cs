namespace ExtremeEditor.Rendering;

public static class AssetCache
{
    public const string TileFileName = "tile.png";
    public const string PerlinFileName = "perlin.png";
    public const string RampFileName = "ramp.png";
    public const string GlowFileName = "glow.png";

    public static string FloorMeshDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "floor-mesh");

    public static string PathFor(string canonicalName) => Path.Combine(FloorMeshDirectory, canonicalName);
}
