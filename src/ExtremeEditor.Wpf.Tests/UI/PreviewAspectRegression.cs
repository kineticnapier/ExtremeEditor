using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Preview aspect modes. "Fit area" renders into the whole preview area (the
/// original behaviour). "16:9" gives the native renderer a real 16:9 target,
/// centred in the area with black bars, so camera-relative decorations,
/// lockScale, masks and blending all use a stock-sized 16:9 screen and the
/// bars never reach native input.
/// </summary>
internal static class PreviewAspectRegression
{
    public static void Run()
    {
        VerifyRectangles();
        VerifyInputMapping();
        VerifyPanelLayout();
        VerifySettings();
        VerifyMainWindowMenu();
        VerifyRenderTarget();
        Console.WriteLine("PASS: preview aspect modes (fit area / 16:9) layout, input, settings and rendering.");
    }

    private static void VerifyRectangles()
    {
        // Wide area: full height, centred horizontally.
        RequireRect(PreviewAspectLayout.Compute(1920, 600, PreviewAspectMode.Fixed16x9),
            new PreviewRenderRect(426, 0, 1067, 600), "wide area");
        // Tall area (the reported 820x746 preview): full width, centred vertically.
        RequireRect(PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.Fixed16x9),
            new PreviewRenderRect(0, 142, 820, 461), "tall area");
        // Already 16:9: no bars.
        RequireRect(PreviewAspectLayout.Compute(1280, 720, PreviewAspectMode.Fixed16x9),
            new PreviewRenderRect(0, 0, 1280, 720), "exact 16:9 area");
        // Fit area keeps the whole area.
        RequireRect(PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.FitArea),
            new PreviewRenderRect(0, 0, 820, 746), "fit area");
        // Degenerate sizes never produce an empty target.
        PreviewRenderRect tiny = PreviewAspectLayout.Compute(1, 1, PreviewAspectMode.Fixed16x9);
        Require(tiny.Width >= 1 && tiny.Height >= 1, $"1x1 area gave {tiny}.");

        // Window resizing: every area keeps the largest centred 16:9 rectangle.
        for (int width = 200; width <= 2600; width += 37)
        {
            for (int height = 150; height <= 1600; height += 53)
            {
                PreviewRenderRect rect = PreviewAspectLayout.Compute(width, height, PreviewAspectMode.Fixed16x9);
                Require(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= width && rect.Y + rect.Height <= height,
                    $"{width}x{height}: {rect} leaves the area.");
                Require(rect.Width == width || rect.Height == height,
                    $"{width}x{height}: {rect} is not the largest 16:9 rectangle.");
                Require(Math.Abs(rect.Width * 9.0 - rect.Height * 16.0) <= 16.0,
                    $"{width}x{height}: {rect} is not 16:9.");
                Require(Math.Abs(rect.X * 2 + rect.Width - width) <= 1 &&
                        Math.Abs(rect.Y * 2 + rect.Height - height) <= 1,
                    $"{width}x{height}: {rect} is not centred.");
            }
        }
    }

    private static void VerifyInputMapping()
    {
        PreviewRenderRect rect = PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.Fixed16x9);
        // Bars are excluded.
        Require(!PreviewAspectLayout.TryMapToRender(400, 10, rect, out _, out _), "top bar click was accepted.");
        Require(!PreviewAspectLayout.TryMapToRender(400, 141.5, rect, out _, out _), "top bar edge click was accepted.");
        Require(!PreviewAspectLayout.TryMapToRender(400, 603.5, rect, out _, out _), "bottom bar click was accepted.");
        // Render coordinates are relative to the render rectangle.
        Require(PreviewAspectLayout.TryMapToRender(0, 142, rect, out double x0, out double y0) && x0 == 0 && y0 == 0,
            "render origin does not map to (0,0).");
        Require(PreviewAspectLayout.TryMapToRender(410, 372.5, rect, out double cx, out double cy) &&
                cx == 410 && cy == 230.5,
            $"render centre mapped to ({cx},{cy}).");
        // Fit area maps one to one.
        PreviewRenderRect fit = PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.FitArea);
        Require(PreviewAspectLayout.TryMapToRender(400, 10, fit, out double fx, out double fy) && fx == 400 && fy == 10,
            "fit area input is not one to one.");
    }

    private static void VerifyPanelLayout()
    {
        // A hit-testable stand-in for the native viewport HWND host.
        var child = new Border { Background = Brushes.Transparent };
        var panel = new PreviewAspectPanel { Mode = PreviewAspectMode.Fixed16x9 };
        panel.Children.Add(child);
        Layout(panel, 820, 746);
        RequireArranged(child, new Rect(0, 142, 820, 461), "16:9 in 820x746");
        Require(panel.Background is SolidColorBrush { Color: var bar } && bar == Colors.Black,
            "letterbox bars are not black.");
        Require(panel.RenderRect == new PreviewRenderRect(0, 142, 820, 461), $"panel render rect {panel.RenderRect}.");

        // The bars belong to the panel, never to the hosted renderer.
        Require(ReferenceEquals(HitTest(panel, new Point(400, 20)), panel), "top bar hit the renderer.");
        Require(ReferenceEquals(HitTest(panel, new Point(400, 700)), panel), "bottom bar hit the renderer.");
        Require(ReferenceEquals(HitTest(panel, new Point(400, 373)), child), "render area did not hit the renderer.");

        // Resizing keeps the target 16:9.
        Layout(panel, 1920, 600);
        RequireArranged(child, new Rect(426, 0, 1067, 600), "16:9 in 1920x600");

        // Switching modes re-arranges the renderer.
        panel.Mode = PreviewAspectMode.FitArea;
        Layout(panel, 820, 746);
        RequireArranged(child, new Rect(0, 0, 820, 746), "fit area in 820x746");
        Require(ReferenceEquals(HitTest(panel, new Point(400, 20)), child), "fit area top did not hit the renderer.");
        panel.Mode = PreviewAspectMode.Fixed16x9;
        Layout(panel, 820, 746);
        RequireArranged(child, new Rect(0, 142, 820, 461), "back to 16:9");

        // The native renderer's pixel extent must equal the render rectangle
        // at fractional DPI, so the GUI target size is exactly the layout size.
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            foreach ((double w, double h) in new[] { (820.0, 746.0), (1013.3, 517.7), (640.0, 900.0) })
            {
                int areaW = (int)NativeLevelViewport.ToPixelExtent(w, dpi);
                int areaH = (int)NativeLevelViewport.ToPixelExtent(h, dpi);
                PreviewRenderRect rect = PreviewAspectLayout.Compute(areaW, areaH, PreviewAspectMode.Fixed16x9);
                Rect dip = PreviewAspectLayout.ToDipRect(rect, dpi, dpi);
                Require(NativeLevelViewport.ToPixelExtent(dip.Width, dpi) == rect.Width &&
                        NativeLevelViewport.ToPixelExtent(dip.Height, dpi) == rect.Height,
                    $"dpi {dpi} area {w}x{h}: DIP {dip} does not give {rect.Width}x{rect.Height} pixels.");
            }
        }
    }

    private static void VerifySettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-PreviewAspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "preview-aspect.json");
            Require(PreviewAspectSettings.Load(path) == PreviewAspectMode.FitArea, "missing settings must default to fit area.");
            PreviewAspectSettings.Save(path, PreviewAspectMode.Fixed16x9);
            Require(PreviewAspectSettings.Load(path) == PreviewAspectMode.Fixed16x9, "16:9 was not restored.");
            PreviewAspectSettings.Save(path, PreviewAspectMode.FitArea);
            Require(PreviewAspectSettings.Load(path) == PreviewAspectMode.FitArea, "fit area was not restored.");
            File.WriteAllText(path, "{ not json");
            Require(PreviewAspectSettings.Load(path) == PreviewAspectMode.FitArea, "corrupt settings must fall back to fit area.");
            File.WriteAllText(path, "\"cinema\"");
            Require(PreviewAspectSettings.Load(path) == PreviewAspectMode.FitArea, "unknown settings must fall back to fit area.");
            // A missing directory is created on save.
            string nested = Path.Combine(directory, "nested", "preview-aspect.json");
            PreviewAspectSettings.Save(nested, PreviewAspectMode.Fixed16x9);
            Require(PreviewAspectSettings.Load(nested) == PreviewAspectMode.Fixed16x9, "save did not create its directory.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void VerifyMainWindowMenu()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-PreviewAspectUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string? previousOverride = PreviewAspectSettings.PathOverride;
        PreviewAspectSettings.PathOverride = Path.Combine(directory, "preview-aspect.json");
        try
        {
            var window = new MainWindow();
            try
            {
                MenuItem view = Find<MenuItem>(window, "ViewMenu");
                MenuItem aspect = Find<MenuItem>(window, "ViewPreviewAspectMenu");
                Require(view.Items.Contains(aspect), "preview aspect menu is not under View.");
                MenuItem fit = Find<MenuItem>(window, "ViewPreviewAspectFitMenuItem");
                MenuItem fixed16x9 = Find<MenuItem>(window, "ViewPreviewAspect16x9MenuItem");
                Require(aspect.Items.Contains(fit) && aspect.Items.Contains(fixed16x9), "aspect items are missing.");
                PreviewAspectPanel host = Find<PreviewAspectPanel>(window, "PreviewAspectHost");
                Require(host.Children.Contains(Find<NativeLevelViewport>(window, "NativeViewport")),
                    "the native viewport is not hosted by the aspect panel.");

                // Default keeps the existing behaviour.
                Require(host.Mode == PreviewAspectMode.FitArea, "default mode is not fit area.");
                Require(fit.IsChecked && !fixed16x9.IsChecked, "default menu check state is wrong.");

                // Switching while playing must not stop playback.
                SetField(window, "_silentPlaybackActive", true);
                SetField(window, "_silentPlaybackPlaying", true);
                fixed16x9.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fixed16x9));
                Require(host.Mode == PreviewAspectMode.Fixed16x9, "16:9 menu did not switch the preview.");
                Require(fixed16x9.IsChecked && !fit.IsChecked, "16:9 menu check state is wrong.");
                Require(GetField<bool>(window, "_silentPlaybackActive") && GetField<bool>(window, "_silentPlaybackPlaying"),
                    "switching the aspect stopped playback.");
                Require(PreviewAspectSettings.Load(PreviewAspectSettings.PathOverride) == PreviewAspectMode.Fixed16x9,
                    "16:9 selection was not saved.");
                SetField(window, "_silentPlaybackActive", false);
                SetField(window, "_silentPlaybackPlaying", false);
            }
            finally
            {
                window.Close();
            }

            // The next start restores the saved mode.
            var restored = new MainWindow();
            try
            {
                Require(Find<PreviewAspectPanel>(restored, "PreviewAspectHost").Mode == PreviewAspectMode.Fixed16x9,
                    "16:9 was not restored at start-up.");
                MenuItem fit = Find<MenuItem>(restored, "ViewPreviewAspectFitMenuItem");
                Require(Find<MenuItem>(restored, "ViewPreviewAspect16x9MenuItem").IsChecked && !fit.IsChecked,
                    "restored menu check state is wrong.");
                fit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fit));
                Require(Find<PreviewAspectPanel>(restored, "PreviewAspectHost").Mode == PreviewAspectMode.FitArea,
                    "fit area menu did not switch back.");
                Require(PreviewAspectSettings.Load(PreviewAspectSettings.PathOverride) == PreviewAspectMode.FitArea,
                    "fit area selection was not saved.");
            }
            finally
            {
                restored.Close();
            }
        }
        finally
        {
            PreviewAspectSettings.PathOverride = previousOverride;
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // The GUI viewport and headless rendering share the native renderer; the
    // GUI simply renders at the 16:9 rectangle's pixel size. A camera-relative
    // black square far to the left (like Phantigma tag 60) enters a 820x746
    // target but stays off a 16:9 target: its position follows the target
    // width (stock screenRelativePos = position / 20 + 0.5 over the camera
    // pixelRect) while its lockScale size follows the target height.
    private static void VerifyRenderTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-PreviewAspectRender-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            WriteSolidBitmap(Path.Combine(directory, "solid.bmp"));
            string chart = Path.Combine(directory, "aspect.adofai");
            File.WriteAllText(chart,
                "{\"angleData\":[0,0,0,0,0,0],\"settings\":{\"bpm\":60,\"zoom\":200,\"backgroundColor\":\"808080\"}," +
                "\"decorations\":[{ \"floor\": 0, \"eventType\": \"AddDecoration\", \"decorationImage\": \"solid.bmp\", " +
                "\"relativeTo\": \"Camera\", \"position\": [-25, 0], \"scale\": [16000, 8000], \"color\": \"000000\", " +
                "\"opacity\": 100, \"visible\": true, \"depth\": -1000, \"lockScale\": true }]}");

            PreviewRenderRect fit = PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.FitArea);
            PreviewRenderRect wide = PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.Fixed16x9);
            RenderedFrame fitFrame = Render(chart, fit, 0);
            RenderedFrame wideFrame = Render(chart, wide, 0);
            Require(fitFrame.Width == 820 && fitFrame.Height == 746, "fit target size changed.");
            Require(wideFrame.Width == 820 && wideFrame.Height == 461, "16:9 target is not 820x461.");
            Require(IsBlack(fitFrame, 40, fitFrame.Height / 6), "fit area lost the camera-relative square.");
            Require(!IsBlack(wideFrame, 40, wideFrame.Height / 6), "16:9 target still shows the off-screen square.");

            VerifyPhantigma();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void VerifyPhantigma()
    {
        string chart = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "4hf3bmk", "main.adofai");
        if (!File.Exists(chart))
        {
            Console.WriteLine("SKIP: Phantigma main.adofai is not present on this machine.");
            return;
        }

        RenderedFrame fit = Render(chart, PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.FitArea), 29);
        RenderedFrame wide = Render(chart, PreviewAspectLayout.Compute(820, 746, PreviewAspectMode.Fixed16x9), 29);
        // The tag 60 square leaves the same black run on every row (a vertical
        // edge); the scene's circular vignette darkens the corners unevenly.
        int[] fitRuns = BlackRuns(fit);
        int[] wideRuns = BlackRuns(wide);
        Console.WriteLine(
            $"Phantigma 29 s: fit area black runs [{string.Join(",", fitRuns)}], " +
            $"16:9 black runs [{string.Join(",", wideRuns)}].");
        Require(HasVerticalBlackEdge(fitRuns), "Phantigma fit area should show the tag 60 square's vertical edge.");
        Require(!HasVerticalBlackEdge(wideRuns) && wideRuns.Max() < 200,
            "Phantigma 16:9 still shows the tag 60 square.");
    }

    private static int[] BlackRuns(RenderedFrame frame) =>
        new[] { 0.1, 0.3, 0.5 }.Select(row => BlackColumnsAt(frame, (int)(frame.Height * row))).ToArray();

    private static bool HasVerticalBlackEdge(int[] runs) =>
        runs.Min() >= 200 && runs.Max() - runs.Min() <= 1;

    private static RenderedFrame Render(string chart, PreviewRenderRect rect, double time)
    {
        using var renderer = new NativeChartFrameRenderer(rect.Width, rect.Height);
        renderer.Load(chart);
        return renderer.Render(time);
    }

    private static bool IsBlack(RenderedFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 3;
        return frame.Rgb[offset] + frame.Rgb[offset + 1] + frame.Rgb[offset + 2] == 0;
    }

    private static int BlackColumnsAt(RenderedFrame frame, int y)
    {
        int count = 0;
        while (count < frame.Width && IsBlack(frame, count, y))
            count++;
        return count;
    }

    private static void Layout(UIElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static object? HitTest(Visual root, Point point)
    {
        HitTestResult? result = VisualTreeHelper.HitTest(root, point);
        return result?.VisualHit;
    }

    private static void RequireArranged(FrameworkElement child, Rect expected, string label)
    {
        Vector offset = VisualTreeHelper.GetOffset(child);
        var actual = new Rect(offset.X, offset.Y, child.RenderSize.Width, child.RenderSize.Height);
        Require(Math.Abs(actual.X - expected.X) < 0.01 && Math.Abs(actual.Y - expected.Y) < 0.01 &&
                Math.Abs(actual.Width - expected.Width) < 0.01 && Math.Abs(actual.Height - expected.Height) < 0.01,
            $"{label}: renderer arranged at {actual}, expected {expected}.");
    }

    private static void RequireRect(PreviewRenderRect actual, PreviewRenderRect expected, string label) =>
        Require(actual == expected, $"{label}: {actual}, expected {expected}.");

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name} {name}.");

    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.")).SetValue(target, value);

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.")).GetValue(target)!;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Preview aspect: " + message);
    }

    private static void WriteSolidBitmap(string path)
    {
        const int size = 32;
        const int pixelBytes = size * size * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42);
        writer.Write(14 + 40 + pixelBytes);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (int i = 0; i < size * size; i++)
            writer.Write(0xFFFFFFFFu);
    }
}
