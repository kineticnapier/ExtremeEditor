using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf;

internal sealed class WpfIconRenderer
{
    private static readonly Dictionary<string, BitmapSource> BitmapCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object BitmapCacheLock = new();

    public WpfIconRenderer()
    {
        EventIconCount = CountPngs(IconAssetCache.EventDirectory);
        FloorIconCount = CountPngs(IconAssetCache.FloorDirectory);
    }

    public int EventIconCount { get; }
    public int FloorIconCount { get; }
    public string Summary => EventIconCount == 0 && FloorIconCount == 0
        ? "icons none"
        : $"icons {EventIconCount} event / {FloorIconCount} floor";

    public bool DrawEvent(DrawingContext drawingContext, string eventType, Point center, float zoom)
    {
        BitmapSource? image = GetCachedBitmap(IconAssetCache.EventPath(eventType));
        if (image is null)
            return false;

        DrawImage(drawingContext, image, center, zoom * 0.62f, 0f, flipped: false);
        return true;
    }

    public bool DrawFloorIcon(
        DrawingContext drawingContext,
        string floorIcon,
        Point center,
        float zoom,
        float angleRadians = 0f,
        bool flipped = false)
    {
        float size = zoom * 0.78f;

        BitmapSource? outline = GetCachedBitmap(IconAssetCache.OutlinePath(floorIcon));
        if (outline is not null)
            DrawImage(drawingContext, outline, center, size * 1.04f, angleRadians, flipped);

        BitmapSource? image = GetCachedBitmap(IconAssetCache.FloorPath(floorIcon));
        if (image is null)
            return false;

        DrawImage(drawingContext, image, center, size, angleRadians, flipped);
        return true;
    }

    private static BitmapSource? GetCachedBitmap(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        lock (BitmapCacheLock)
        {
            if (BitmapCache.TryGetValue(fullPath, out BitmapSource? cached))
                return cached;

            if (!File.Exists(fullPath))
                return null;

            try
            {
                using FileStream stream = File.OpenRead(fullPath);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                BitmapCache.Add(fullPath, bitmap);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void DrawImage(
        DrawingContext drawingContext,
        BitmapSource image,
        Point center,
        float requestedSize,
        float angleRadians,
        bool flipped)
    {
        int maxDimension = Math.Max(image.PixelWidth, image.PixelHeight);
        if (maxDimension <= 0)
            return;

        double size = Math.Clamp(requestedSize, 10f, 96f);
        double scale = size / maxDimension;
        double width = image.PixelWidth * scale;
        double height = image.PixelHeight * scale;

        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        double flipX = flipped ? -1.0 : 1.0;
        var matrix = new Matrix(
            flipX * cos,
            flipX * sin,
            -sin,
            cos,
            center.X,
            center.Y);

        drawingContext.PushTransform(new MatrixTransform(matrix));
        drawingContext.DrawImage(image, new Rect(-width * 0.5, -height * 0.5, width, height));
        drawingContext.Pop();
    }

    private static int CountPngs(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
