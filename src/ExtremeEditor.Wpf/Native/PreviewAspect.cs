using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExtremeEditor.Wpf.Native;

/// <summary>How the preview area is used as the game screen.</summary>
public enum PreviewAspectMode
{
    /// <summary>Render into the whole preview area (original behaviour).</summary>
    FitArea = 0,

    /// <summary>Render into the largest centred 16:9 rectangle, black bars around it.</summary>
    Fixed16x9 = 1
}

/// <summary>Render target placement inside the preview area, in device pixels.</summary>
internal readonly record struct PreviewRenderRect(int X, int Y, int Width, int Height)
{
    public bool Contains(double x, double y) =>
        x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// The single source of the preview render target: its pixel size, its
/// position inside the preview area and the area-to-render input mapping.
/// The renderer always receives the target's real width and height, so
/// camera-relative decorations, lockScale, masks and blending see a 16:9
/// screen exactly as stock ADOFAI's camera pixelRect would be.
/// </summary>
internal static class PreviewAspectLayout
{
    private const int AspectWidth = 16;
    private const int AspectHeight = 9;

    public static PreviewRenderRect Compute(int areaWidth, int areaHeight, PreviewAspectMode mode)
    {
        areaWidth = Math.Max(1, areaWidth);
        areaHeight = Math.Max(1, areaHeight);
        if (mode != PreviewAspectMode.Fixed16x9)
            return new PreviewRenderRect(0, 0, areaWidth, areaHeight);

        int width;
        int height;
        if ((long)areaWidth * AspectHeight >= (long)areaHeight * AspectWidth)
        {
            // Wider than 16:9: full height, bars left and right.
            height = areaHeight;
            width = Math.Clamp(
                (int)Math.Round(areaHeight * (double)AspectWidth / AspectHeight, MidpointRounding.AwayFromZero),
                1, areaWidth);
        }
        else
        {
            // Taller than 16:9: full width, bars above and below.
            width = areaWidth;
            height = Math.Clamp(
                (int)Math.Round(areaWidth * (double)AspectHeight / AspectWidth, MidpointRounding.AwayFromZero),
                1, areaHeight);
        }
        return new PreviewRenderRect((areaWidth - width) / 2, (areaHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Maps a preview-area pixel to render-target pixels. Points on the bars
    /// return false so they never become editor input.
    /// </summary>
    public static bool TryMapToRender(
        double areaX,
        double areaY,
        PreviewRenderRect rect,
        out double renderX,
        out double renderY)
    {
        renderX = areaX - rect.X;
        renderY = areaY - rect.Y;
        return rect.Contains(areaX, areaY);
    }

    /// <summary>
    /// Converts a pixel rectangle to DIPs such that the native host's pixel
    /// extent (NativeLevelViewport.ToPixelExtent, a ceiling) gives back
    /// exactly the rectangle's width and height.
    /// </summary>
    public static Rect ToDipRect(PreviewRenderRect rect, double dpiScaleX, double dpiScaleY)
    {
        dpiScaleX = ValidScale(dpiScaleX);
        dpiScaleY = ValidScale(dpiScaleY);
        return new Rect(
            rect.X / dpiScaleX,
            rect.Y / dpiScaleY,
            ToDipExtent(rect.Width, dpiScaleX),
            ToDipExtent(rect.Height, dpiScaleY));
    }

    private static double ToDipExtent(int pixels, double dpiScale)
    {
        double dip = pixels / dpiScale;
        while (dip > 0 && NativeLevelViewport.ToPixelExtent(dip, dpiScale) > pixels)
            dip = Math.BitDecrement(dip);
        return dip;
    }

    private static double ValidScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1d;
}

/// <summary>Persists the preview aspect mode next to the other editor preferences.</summary>
internal static class PreviewAspectSettings
{
    private const string FitAreaValue = "fit";
    private const string Fixed16x9Value = "16:9";

    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "preview-aspect.json");

    /// <summary>Test hook: replaces <see cref="DefaultPath"/> when set.</summary>
    internal static string? PathOverride { get; set; }

    public static string CurrentPath => PathOverride ?? DefaultPath;

    public static PreviewAspectMode Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return PreviewAspectMode.FitArea;
            return JsonSerializer.Deserialize<string>(File.ReadAllText(path)) switch
            {
                Fixed16x9Value => PreviewAspectMode.Fixed16x9,
                _ => PreviewAspectMode.FitArea
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PreviewAspectMode.FitArea;
        }
    }

    public static void Save(string path, PreviewAspectMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(
                mode == PreviewAspectMode.Fixed16x9 ? Fixed16x9Value : FitAreaValue));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Hosts the native preview. In 16:9 mode its child (the native viewport's
/// HWND) is arranged to the 16:9 render rectangle, so the renderer's actual
/// target is 16:9 and native mouse input is already relative to it; the black
/// bars are this panel's own background and never reach the renderer.
/// </summary>
public sealed class PreviewAspectPanel : Panel
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(PreviewAspectMode),
        typeof(PreviewAspectPanel),
        new FrameworkPropertyMetadata(
            PreviewAspectMode.FitArea,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public PreviewAspectPanel()
    {
        Background = Brushes.Black;
    }

    public PreviewAspectMode Mode
    {
        get => (PreviewAspectMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>The render rectangle of the last arrange, in device pixels.</summary>
    internal PreviewRenderRect RenderRect { get; private set; } = new(0, 0, 1, 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(availableSize);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect childRect = ComputeChildRect(finalSize);
        foreach (UIElement child in InternalChildren)
            child.Arrange(childRect);
        return finalSize;
    }

    private Rect ComputeChildRect(Size finalSize)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int areaWidth = (int)NativeLevelViewport.ToPixelExtent(finalSize.Width, dpi.DpiScaleX);
        int areaHeight = (int)NativeLevelViewport.ToPixelExtent(finalSize.Height, dpi.DpiScaleY);
        PreviewRenderRect rect = PreviewAspectLayout.Compute(areaWidth, areaHeight, Mode);
        RenderRect = rect;
        // Fit area keeps the original layout: the child gets the whole area.
        return Mode == PreviewAspectMode.Fixed16x9
            ? PreviewAspectLayout.ToDipRect(rect, dpi.DpiScaleX, dpi.DpiScaleY)
            : new Rect(finalSize);
    }
}
