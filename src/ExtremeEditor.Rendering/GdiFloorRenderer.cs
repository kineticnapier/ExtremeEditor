using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace ExtremeEditor.Rendering;

/// <summary>
/// First standalone floor preview renderer. It consumes the probed straight-floor
/// geometry and locally imported ADOFAI textures without creating per-floor objects.
/// GDI+ is intentionally an interim backend; the API is kept separate so a batched
/// GPU backend can replace it without touching the level model.
/// </summary>
public sealed class GdiFloorRenderer : IDisposable
{
    private readonly PointF[] _main = new PointF[4];
    private readonly PointF[] _topShadow = new PointF[4];
    private readonly PointF[] _bottomShadow = new PointF[4];
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

    public void DrawFloor(Graphics graphics, PointF center, float zoom, float rotationRadians, bool selected)
    {
        TransformOutline(AdoFaiFloorMesh.MainOutline, _main, center, zoom, rotationRadians);
        TransformOutline(AdoFaiFloorMesh.BottomShadowOutline, _bottomShadow, center, zoom, rotationRadians);
        TransformOutline(AdoFaiFloorMesh.TopShadowOutline, _topShadow, center, zoom, rotationRadians);

        graphics.FillPolygon(_shadowBrush, _bottomShadow);
        graphics.FillPolygon(_shadowBrush, _topShadow);
        Brush mainBrush = _tileBrush is null ? _fallbackBrush : _tileBrush;
        graphics.FillPolygon(mainBrush, _main);

        if (selected)
            graphics.DrawPolygon(_selectedPen, _main);
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

    private static void TransformOutline(int[] indices, PointF[] destination, PointF center, float zoom, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        for (int i = 0; i < indices.Length; i++)
        {
            Vector2 local = AdoFaiFloorMesh.StraightVertices[indices[i]].Position;
            float worldX = local.X * cos - local.Y * sin;
            float worldY = local.X * sin + local.Y * cos;
            destination[i] = new PointF(
                center.X + worldX * zoom,
                center.Y - worldY * zoom);
        }
    }
}
