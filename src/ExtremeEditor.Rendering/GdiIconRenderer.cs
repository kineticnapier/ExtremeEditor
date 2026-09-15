using System.Drawing.Drawing2D;

namespace ExtremeEditor.Rendering;

public sealed class GdiIconRenderer : IDisposable
{
    private readonly Dictionary<string, Bitmap> _eventIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _floorIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _outlineIcons = new(StringComparer.OrdinalIgnoreCase);

    public GdiIconRenderer() => Reload();

    public int EventIconCount => _eventIcons.Count;
    public int FloorIconCount => _floorIcons.Count;
    public string Summary => _eventIcons.Count == 0 && _floorIcons.Count == 0
        ? "icons none"
        : $"icons {_eventIcons.Count} event / {_floorIcons.Count} floor";

    public void Reload()
    {
        DisposeImages(_eventIcons);
        DisposeImages(_floorIcons);
        DisposeImages(_outlineIcons);
        LoadDirectory(IconAssetCache.EventDirectory, _eventIcons);
        LoadDirectory(IconAssetCache.FloorDirectory, _floorIcons);
        LoadDirectory(IconAssetCache.OutlineDirectory, _outlineIcons);
    }

    public bool DrawEvent(Graphics graphics, string eventType, PointF center, float zoom)
    {
        if (!_eventIcons.TryGetValue(eventType, out Bitmap? image))
            return false;
        DrawImage(graphics, image, center, zoom * 0.62f, 0f, flipped: false);
        return true;
    }

    public bool DrawFloorIcon(
        Graphics graphics,
        string floorIcon,
        PointF center,
        float zoom,
        float angleRadians = 0f,
        bool flipped = false)
    {
        float size = zoom * 0.78f;
        if (_outlineIcons.TryGetValue(floorIcon, out Bitmap? outline))
            DrawImage(graphics, outline, center, size * 1.04f, angleRadians, flipped);
        if (!_floorIcons.TryGetValue(floorIcon, out Bitmap? image))
            return false;
        DrawImage(graphics, image, center, size, angleRadians, flipped);
        return true;
    }

    public void Dispose()
    {
        DisposeImages(_eventIcons);
        DisposeImages(_floorIcons);
        DisposeImages(_outlineIcons);
    }

    private static void DrawImage(
        Graphics graphics,
        Image image,
        PointF center,
        float requestedSize,
        float angleRadians,
        bool flipped)
    {
        float maxDimension = Math.Max(image.Width, image.Height);
        if (maxDimension <= 0) return;

        float size = Math.Clamp(requestedSize, 10f, 96f);
        float scale = size / maxDimension;
        float width = image.Width * scale;
        float height = image.Height * scale;

        GraphicsState state = graphics.Save();
        try
        {
            InterpolationMode previous = graphics.InterpolationMode;
            PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // ADOFAI's mesh icon angle uses 0 = up and positive angles turning
            // clockwise on screen. GDI+ has the same visible rotation direction
            // after translating into screen coordinates.
            graphics.TranslateTransform(center.X, center.Y);
            if (Math.Abs(angleRadians) > 0.000001f)
                graphics.RotateTransform(angleRadians * (180f / MathF.PI));
            if (flipped)
                graphics.ScaleTransform(-1f, 1f);

            graphics.DrawImage(image, new RectangleF(-width * .5f, -height * .5f, width, height));
            graphics.InterpolationMode = previous;
            graphics.PixelOffsetMode = previousOffset;
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void LoadDirectory(string directory, Dictionary<string, Bitmap> target)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var source = new Bitmap(stream);
                target[Path.GetFileNameWithoutExtension(path)] = new Bitmap(source);
            }
            catch
            {
                // A bad locally extracted icon must not prevent the editor from opening.
            }
        }
    }

    private static void DisposeImages(Dictionary<string, Bitmap> images)
    {
        foreach (Bitmap image in images.Values)
            image.Dispose();
        images.Clear();
    }
}
