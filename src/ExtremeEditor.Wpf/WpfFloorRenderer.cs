using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf;

internal sealed class WpfFloorRenderer
{
    private const float TwoPi = MathF.PI * 2f;

    private static readonly Dictionary<GeometryKey, StreamGeometry> GeometryCache = new();
    private static readonly SolidColorBrush FallbackBrush = CreateBrush(225, 228, 235, 235);
    private static readonly SolidColorBrush EdgeBrush = CreateBrush(24, 22, 18, 105);
    private static readonly SolidColorBrush SelectedBrush = CreateBrush(255, 210, 80);

    private readonly Brush _mainBrush;
    private Pen _edgePen = CreatePen(EdgeBrush, 0.022);
    private Pen _selectedPen = CreatePen(SelectedBrush, 0.08);
    private float _preparedZoom = -1f;

    public WpfFloorRenderer()
    {
        _mainBrush = LoadTileBrush() ?? FallbackBrush;
    }

    public bool HasImportedAssets => !ReferenceEquals(_mainBrush, FallbackBrush);
    public string AssetSummary => HasImportedAssets ? "tile asset" : "fallback material";

    public void BeginFrame(float zoom)
    {
        zoom = Math.Max(0.0001f, zoom);
        if (MathF.Abs(_preparedZoom - zoom) <= 0.0001f)
            return;

        _preparedZoom = zoom;
        double edgePixels = Math.Clamp(zoom * 0.022f, 1f, 5f);
        _edgePen = CreatePen(EdgeBrush, edgePixels / zoom);
        _selectedPen = CreatePen(SelectedBrush, 2.0 / zoom);
    }

    public void DrawFloor(
        DrawingContext drawingContext,
        Point center,
        float zoom,
        float entryAngle,
        float exitAngle,
        bool midSpin,
        bool selected)
    {
        StreamGeometry geometry = GetCachedGeometry(entryAngle, exitAngle, midSpin);

        float cos = MathF.Cos(entryAngle);
        float sin = MathF.Sin(entryAngle);
        var matrix = new Matrix(
            zoom * cos,
            -zoom * sin,
            -zoom * sin,
            -zoom * cos,
            center.X,
            center.Y);

        drawingContext.PushTransform(new MatrixTransform(matrix));
        drawingContext.DrawGeometry(_mainBrush, _edgePen, geometry);
        if (selected)
            drawingContext.DrawGeometry(null, _selectedPen, geometry);
        drawingContext.Pop();
    }

    private static StreamGeometry GetCachedGeometry(float entryAngle, float exitAngle, bool midSpin)
    {
        float delta = Mod(exitAngle - entryAngle, TwoPi);
        var key = new GeometryKey((int)MathF.Round(delta * 100_000f), midSpin);
        if (GeometryCache.TryGetValue(key, out StreamGeometry? cached))
            return cached;

        FloorGeometry source = AdoFaiFloorGeometryBuilder.Get(0f, delta, midSpin);
        var geometry = new StreamGeometry
        {
            FillRule = FillRule.EvenOdd
        };

        if (source.Main.Length > 0)
        {
            using StreamGeometryContext context = geometry.Open();
            Vector2 first = source.Main[0];
            context.BeginFigure(new Point(first.X, first.Y), isFilled: true, isClosed: true);
            for (int i = 1; i < source.Main.Length; i++)
            {
                Vector2 point = source.Main[i];
                context.LineTo(new Point(point.X, point.Y), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        GeometryCache.Add(key, geometry);
        return geometry;
    }

    private static Brush? LoadTileBrush()
    {
        string path = AssetCache.PathFor(AssetCache.TileFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly record struct GeometryKey(int Delta, bool MidSpin);
}
