using System.IO;
using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class PhantigmaIssue15DecorationRegression
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_PHANTIGMA_ISSUE15_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        string chartPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "4hf3bmk", "main.adofai");
        if (!File.Exists(chartPath))
        {
            Console.WriteLine("SKIP: Phantigma main.adofai is not present on this machine.");
            return;
        }

        WpfLevelLoadResult loaded = WpfLevelLoader.Load(chartPath);
        LevelDocument level = loaded.Document;
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        LevelDecoration square = level.Decorations.Single(decoration =>
            string.Equals(decoration.Properties["decorationImage"]?.ToString(), "square.png", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(decoration.Properties["tag"]?.ToString(), "60", StringComparison.Ordinal));

        DecorationState state = DecorationState.Evaluate(square, timeline, 79.571);
        Vector2 screenRelative = DecorationWorldTransformResolver.ResolveScreenRelativePosition(
            square, state, 960, 540);
        RequireNear(state.PositionX, -25.5, 0.001,
            "Phantigma tag 60 positionOffset did not apply tileSize 1.5.");
        RequireNear(screenRelative.X, -0.775, 0.0001,
            "Phantigma tag 60 center did not reach -0.775 viewport widths.");

        // Stock lockScale: 256 px * scale * Screen.height * 0.001 / (chart zoom / 100).
        float chartZoomFactor = StaticDecorationSnapshotBuilder.LockScaleChartZoomFactor(level, lockScale: true);
        RequireNear(chartZoomFactor, 0.5, 0.0001,
            "Phantigma lockScale did not use the chart settings zoom 200.");
        double widthPixels = 256 * 20 * 540 * 0.001 * chartZoomFactor;
        double heightPixels = 256 * 10 * 540 * 0.001 * chartZoomFactor;
        RequireNear(widthPixels, 1382.4, 0.01, "Phantigma tag 60 width differs from stock.");
        RequireNear(heightPixels, 691.2, 0.01, "Phantigma tag 60 height differs from stock.");
        double rightEdge = screenRelative.X * 960 + widthPixels * 0.5;
        RequireNear(rightEdge, -52.8, 0.01,
            "Phantigma tag 60 right edge remained inside the viewport.");

        string outputDirectory = Path.Combine(
            Environment.CurrentDirectory, "build", "issue15-phantigma");
        Directory.CreateDirectory(outputDirectory);
        using var renderer = new NativeChartFrameRenderer(960, 540);
        renderer.Load(level, FlatTimingMapBuilder.Build(level));
        SaveAndValidate(renderer.Render(79.571), Path.Combine(outputDirectory, "phantigma-79.571.png"));
        SaveAndValidate(renderer.Render(106.5), Path.Combine(outputDirectory, "phantigma-106.5.png"));

        Console.WriteLine(
            $"PASS: Phantigma issue #15 centerX={screenRelative.X:F3} viewport, " +
            $"size={widthPixels:F1}x{heightPixels:F1}px, right={rightEdge:F1}px; " +
            $"frames={outputDirectory}");
    }

    private static void SaveAndValidate(RenderedFrame frame, string path)
    {
        if (frame.Width != 960 || frame.Height != 540 || frame.Rgb.Length != 960 * 540 * 3)
            throw new InvalidOperationException("Phantigma headless frame dimensions are invalid.");

        ReadOnlySpan<byte> rgb = frame.Rgb;
        bool differs = false;
        for (int index = 3; index < rgb.Length; index += 3)
        {
            if (rgb[index] != rgb[0] || rgb[index + 1] != rgb[1] || rgb[index + 2] != rgb[2])
            {
                differs = true;
                break;
            }
        }
        if (!differs)
            throw new InvalidOperationException($"Phantigma frame at {path} is a uniform image.");

        ChartFramePngWriter.Save(frame, path);
    }

    private static void RequireNear(double actual, double expected, double tolerance, string message)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{message} actual={actual:G17}, expected={expected:G17}");
    }
}
