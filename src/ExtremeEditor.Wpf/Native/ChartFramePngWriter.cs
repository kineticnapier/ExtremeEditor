using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public static class ChartFramePngWriter
{
    public static void Save(RenderedFrame frame, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame));

        int stride = checked(frame.Width * 3);
        int requiredBytes = checked(stride * frame.Height);
        if (frame.Rgb.Length != requiredBytes)
        {
            throw new ArgumentException(
                $"RGB888 frame length mismatch. Expected {requiredBytes}, got {frame.Rgb.Length}.",
                nameof(frame));
        }

        BitmapSource bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96.0,
            96.0,
            PixelFormats.Rgb24,
            palette: null,
            frame.Rgb,
            stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream stream = File.Create(fullPath);
        encoder.Save(stream);
    }
}
