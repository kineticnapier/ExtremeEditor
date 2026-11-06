using System.Diagnostics;
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
        VerifyTimingParity(level);
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
        string? previousMetrics = Environment.GetEnvironmentVariable(
            "EXTREMEEDITOR_DECORATION_FRAME_METRICS");
        Environment.SetEnvironmentVariable("EXTREMEEDITOR_DECORATION_FRAME_METRICS", "1");
        try
        {
            VerifyHeadlessParityAndMeasure(level, timeline, outputDirectory);
            Measure1080p60(level);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "EXTREMEEDITOR_DECORATION_FRAME_METRICS", previousMetrics);
        }

        Console.WriteLine(
            $"PASS: Phantigma issue #15 centerX={screenRelative.X:F3} viewport, " +
            $"size={widthPixels:F1}x{heightPixels:F1}px, right={rightEdge:F1}px; " +
            $"frames={outputDirectory}");
    }

    private static void VerifyHeadlessParityAndMeasure(
        LevelDocument level,
        VfxTimeline timeline,
        string outputDirectory)
    {
        double[] requestedTimes = [8.01, 8.2, 9.2, 79.571, 106.5];
        StaticDecorationSnapshotData gui = StaticDecorationSnapshotBuilder.Build(
            level, timeline, double.NegativeInfinity);
        using var renderer = new NativeChartFrameRenderer(960, 540);
        renderer.Load(level, FlatTimingMapBuilder.Build(level));
        var evaluation = new List<double>();
        var upload = new List<double>();

        foreach (double time in requestedTimes.Concat(requestedTimes.AsEnumerable().Reverse()))
        {
            RenderedFrame frame = renderer.Render(time);
            gui.PlaybackRuntime?.Update(time);
            CompareDecorationArrays(gui.Instances, renderer.StaticDecorations, time);
            NativeDecorationFrameMetrics metrics = renderer.LastDecorationFrameMetrics;
            if (metrics.Uploaded)
            {
                evaluation.Add(metrics.Evaluation.TotalMilliseconds);
                upload.Add(metrics.Upload.TotalMilliseconds);
            }

            if (time == 79.571 || time == 106.5)
            {
                SaveAndValidate(
                    frame,
                    Path.Combine(outputDirectory, $"phantigma-{time:G}.png"));
            }
        }

        Console.WriteLine(
            $"Phantigma decoration frames: animated={renderer.LastDecorationFrameMetrics.AnimatedCount}, " +
            $"samples={evaluation.Count}, evaluation avg/max={evaluation.Average():F3}/{evaluation.Max():F3}ms, " +
            $"native upload avg/max={upload.Average():F3}/{upload.Max():F3}ms.");
        Console.WriteLine(
            "Phantigma GUI/headless decoration arrays matched at " +
            string.Join(", ", requestedTimes.Select(time => $"{time:G}s")) +
            " and in reverse seek order.");
    }

    private static void Measure1080p60(LevelDocument level)
    {
        using var renderer = new NativeChartFrameRenderer(1920, 1080);
        renderer.Load(level, FlatTimingMapBuilder.Build(level));
        var pipeline = new VideoExportFramePipeline(renderer);
        var plan = new VideoExportPlan(8.01, 9.01, 60, 1920, 1080);
        double evaluationMs = 0;
        double uploadMs = 0;
        int frames = 0;
        var watch = Stopwatch.StartNew();
        pipeline.RenderAsync(
                plan,
                (_, _, _) =>
                {
                    NativeDecorationFrameMetrics metrics = renderer.LastDecorationFrameMetrics;
                    evaluationMs += metrics.Evaluation.TotalMilliseconds;
                    uploadMs += metrics.Upload.TotalMilliseconds;
                    frames++;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        watch.Stop();

        Console.WriteLine(
            $"Phantigma 1080p60 RGB video frames: {frames} frames in {watch.Elapsed.TotalSeconds:F3}s " +
            $"({frames / watch.Elapsed.TotalSeconds:F1} fps); decoration evaluation={evaluationMs:F3}ms, " +
            $"native upload={uploadMs:F3}ms total.");
    }

    private static void CompareDecorationArrays(
        NativeStaticDecoration[] expected,
        NativeStaticDecoration[] actual,
        double time)
    {
        if (expected.Length != actual.Length)
        {
            throw new InvalidOperationException(
                $"Phantigma decoration count differs at {time:G}s: {expected.Length}/{actual.Length}.");
        }

        for (int index = 0; index < expected.Length; index++)
        {
            NativeStaticDecoration left = expected[index];
            NativeStaticDecoration right = actual[index];
            bool equal = left.SourceIndex == right.SourceIndex &&
                         left.Floor == right.Floor &&
                         left.AssetId == right.AssetId &&
                         left.RelativeMode == right.RelativeMode &&
                         Near(left.PositionX, right.PositionX) &&
                         Near(left.PositionY, right.PositionY) &&
                         Near(left.ScaleX, right.ScaleX) &&
                         Near(left.ScaleY, right.ScaleY) &&
                         Near(left.RotationRadians, right.RotationRadians) &&
                         Near(left.PivotOffsetX, right.PivotOffsetX) &&
                         Near(left.PivotOffsetY, right.PivotOffsetY) &&
                         Near(left.Opacity, right.Opacity) &&
                         left.Color == right.Color &&
                         left.Depth == right.Depth &&
                         left.Flags == right.Flags;
            if (!equal)
            {
                throw new InvalidOperationException(
                    $"GUI/headless decoration state differs at {time:G}s, source {left.SourceIndex}.");
            }
        }
    }

    private static void VerifyTimingParity(LevelDocument level)
    {
        TimingMap regular = TimingMapBuilder.Build(level);
        TimingMap flat = FlatTimingMapBuilder.Build(level);
        if (regular.Floors.Count != flat.Floors.Count)
            throw new InvalidOperationException("Phantigma timing builders returned different floor counts.");
        for (int index = 0; index < regular.Floors.Count; index++)
        {
            if (Math.Abs(regular.Floors[index].EntryTime - flat.Floors[index].EntryTime) > 1e-9 ||
                Math.Abs(regular.Floors[index].ExitTime - flat.Floors[index].ExitTime) > 1e-9)
            {
                throw new InvalidOperationException(
                    $"Phantigma TimingMapBuilder/FlatTimingMapBuilder differ at floor {index}.");
            }
        }
        Console.WriteLine(
            $"Phantigma timing maps: {regular.Floors.Count:N0} floors matched numerically.");
    }

    private static bool Near(float left, float right) =>
        Math.Abs(left - right) <= 0.000001f;

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
