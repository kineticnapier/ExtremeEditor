using System.Drawing;

namespace ExtremeEditor.Rendering;

public sealed class FloorTextureSet : IDisposable
{
    public Bitmap? Tile { get; }
    public Bitmap? Perlin { get; }
    public Bitmap? Ramp { get; }
    public Bitmap? Glow { get; }

    public bool HasTile => Tile is not null;
    public bool IsComplete => Tile is not null && Perlin is not null && Ramp is not null && Glow is not null;

    private FloorTextureSet(Bitmap? tile, Bitmap? perlin, Bitmap? ramp, Bitmap? glow)
    {
        Tile = tile;
        Perlin = perlin;
        Ramp = ramp;
        Glow = glow;
    }

    public static FloorTextureSet LoadFromCache() => new(
        LoadBitmap(AssetCache.PathFor(AssetCache.TileFileName)),
        LoadBitmap(AssetCache.PathFor(AssetCache.PerlinFileName)),
        LoadBitmap(AssetCache.PathFor(AssetCache.RampFileName)),
        LoadBitmap(AssetCache.PathFor(AssetCache.GlowFileName)));

    public void Dispose()
    {
        Tile?.Dispose();
        Perlin?.Dispose();
        Ramp?.Dispose();
        Glow?.Dispose();
    }

    private static Bitmap? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        using var source = Image.FromFile(path);
        return new Bitmap(source);
    }
}
