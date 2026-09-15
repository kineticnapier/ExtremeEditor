using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace ExtremeEditor.Rendering;

/// <summary>
/// Standalone floor preview renderer. The visible floor silhouette is generated from
/// the incoming/outgoing path rays using ADOFAI-compatible corner geometry. GDI+ is
/// still an interim backend; no per-floor UI objects are created.
/// </summary>
public sealed class GdiFloorRenderer : IDisposable
{
    private readonly SolidBrush _fallbackBrush = new(Color.FromArgb(235, 225, 228, 235));
    private readonly SolidBrush _shadowBrush = new(Color.FromArgb(115, 0, 0, 0));
    private readonly Pen _selectedPen = new(Color.FromArgb(255, 255, 210, 80), 2f);

    private FloorTextureSet _textures = FloorTextureSet.LoadFromCache();
    private TextureBrush? _tileBrush;

    public bool HasImportedAssets => _textures.HasTile;
    public bool HasCompleteAssetSet => _textures.IsComplete;
    public string AssetSummary => HasCompleteAssetSet ? "assets complete" : HasImportedAssets ? "tile asset" : "fallback material";

    public GdiFloorRenderer() => RebuildBrush();

    public void ReloadAssets()
    {
        _tileBrush?.Dispose();
        _textures.Dispose();
        _textures = FloorTextureSet.LoadFromCache();
        RebuildBrush();
    }

    public void BeginFrame(float zoom)
    {
        if (_tileBrush is null) return;
        _tileBrush.ResetTransform();
        float scale = Math.Clamp(zoom / 320f, 0.02f, 2f);
        _tileBrush.ScaleTransform(scale, scale, MatrixOrder.Append);
    }

    public void DrawFloor(
        Graphics graphics,
        PointF center,
        float zoom,
        float entryAngle,
        float exitAngle,
        bool midSpin,
        bool selected)
    {
        FloorGeometry geometry = AdoFaiFloorGeometryBuilder.Get(entryAngle, exitAngle, midSpin);

        // Geometry is cached with entryAngle normalized to zero. Rotate the cached
        // shape back onto this floor's incoming ray.
        foreach (Vector2[] shadow in geometry.Shadows)
        {
            PointF[] transformedShadow = TransformPolygon(shadow, center, zoom, entryAngle);
            if (transformedShadow.Length >= 3)
                graphics.FillPolygon(_shadowBrush, transformedShadow);
        }

        PointF[] main = TransformPolygon(geometry.Main, center, zoom, entryAngle);
        if (main.Length < 3) return;

        Brush mainBrush = _tileBrush is null ? _fallbackBrush : _tileBrush;
        graphics.FillPolygon(mainBrush, main);

        if (selected)
            graphics.DrawPolygon(_selectedPen, main);
    }

    public void Dispose()
    {
        _tileBrush?.Dispose();
        _textures.Dispose();
        _fallbackBrush.Dispose();
        _shadowBrush.Dispose();
        _selectedPen.Dispose();
    }

    private void RebuildBrush()
    {
        _tileBrush = _textures.Tile is null
            ? null
            : new TextureBrush(_textures.Tile, WrapMode.Tile);
    }

    private static PointF[] TransformPolygon(Vector2[] source, PointF center, float zoom, float angle)
    {
        var destination = new PointF[source.Length];
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        for (int i = 0; i < source.Length; i++)
        {
            Vector2 local = source[i];
            float worldX = local.X * cos - local.Y * sin;
            float worldY = local.X * sin + local.Y * cos;
            destination[i] = new PointF(
                center.X + worldX * zoom,
                center.Y - worldY * zoom);
        }
        return destination;
    }
}
