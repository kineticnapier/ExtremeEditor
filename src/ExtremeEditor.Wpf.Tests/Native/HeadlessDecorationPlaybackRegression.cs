using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class HeadlessDecorationPlaybackRegression
{
    private const double Tolerance = 0.0001;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_HEADLESS_DECORATION_PLAYBACK_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: headless decoration playback follows requested chart time.");
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
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-HeadlessDecoration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string chartPath = Path.Combine(directory, "fade.adofai");
            string controlPath = Path.Combine(directory, "control.adofai");
            WriteSolidBitmap(Path.Combine(directory, "fade.bmp"));
            File.WriteAllText(chartPath, CreateFixture());
            File.WriteAllText(controlPath, CreateControlFixture());

            WpfLevelLoadResult loaded = WpfLevelLoader.Load(chartPath);
            LevelDocument level = loaded.Document;
            TimingMap flatTiming = FlatTimingMapBuilder.Build(level);
            VerifyTimingParity(level, flatTiming);
            VfxTimeline timeline = VfxTimelineBuilder.Build(level, flatTiming);
            VfxOccurrence move = timeline.Occurrences.Single(occurrence =>
                occurrence.EventType == "MoveDecorations" && !occurrence.IsRepeated);
            double before = move.StartTime - 0.01;
            double middle = move.StartTime + move.DurationSeconds!.Value * 0.5;
            double after = move.StartTime + move.DurationSeconds.Value + 0.01;

            using var sequential = new NativeChartFrameRenderer(160, 90);
            sequential.Load(level, flatTiming);
            RenderedFrame beforeFrame = sequential.Render(before);
            AssertMatchesGuiState(sequential, level, timeline, before);
            RenderedFrame middleFrame = sequential.Render(middle);
            AssertMatchesGuiState(sequential, level, timeline, middle);
            RenderedFrame afterFrame = sequential.Render(after);
            AssertMatchesGuiState(sequential, level, timeline, after);

            using var control = new NativeChartFrameRenderer(160, 90);
            control.Load(controlPath);
            long beforeDifference = PixelDifference(beforeFrame, control.Render(before));
            long middleDifference = PixelDifference(middleFrame, control.Render(middle));
            long afterDifference = PixelDifference(afterFrame, control.Render(after));
            if (!(beforeDifference == 0 && middleDifference > 0 && afterDifference > middleDifference))
            {
                throw new InvalidOperationException(
                    $"RED: headless opacity pixels did not animate: " +
                    $"{beforeDifference}, {middleDifference}, {afterDifference}.");
            }

            using var direct = new NativeChartFrameRenderer(160, 90);
            direct.Load(level, flatTiming);
            RenderedFrame directMiddle = direct.Render(middle);
            RequireEqual(middleFrame, directMiddle, "direct seek differs from sequential rendering");
            RequireEqual(directMiddle, direct.Render(middle), "same-time render is not deterministic");

            RenderedFrame seekBack = sequential.Render(before);
            RequireEqual(beforeFrame, seekBack, "backward seek retained future decoration state");
            RenderedFrame middleAfterBack = sequential.Render(middle);
            RequireEqual(middleFrame, middleAfterBack, "forward render after backward seek changed state");

            sequential.Reset();
            RequireEqual(middleFrame, sequential.Render(middle),
                "renderer reinitialization changed decoration playback state");

            VerifyVideoFrames(level, flatTiming, move.StartTime, move.DurationSeconds.Value);
            VerifyNoDecorationAndStaticDecoration(directory);
            VerifyRgbaPixels(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort: test correctness does not depend on temp cleanup.
            }
        }
    }

    private static void AssertMatchesGuiState(
        NativeChartFrameRenderer renderer,
        LevelDocument level,
        VfxTimeline timeline,
        double chartTime)
    {
        NativeStaticDecoration expected = StaticDecorationSnapshotBuilder
            .Build(level, timeline, chartTime).Instances.Single();
        NativeStaticDecoration actual = ReadHeadlessInstances(renderer).Single();

        RequireNear(actual.PositionX, expected.PositionX, "position X");
        RequireNear(actual.PositionY, expected.PositionY, "position Y");
        RequireNear(actual.ScaleX, expected.ScaleX, "scale X");
        RequireNear(actual.ScaleY, expected.ScaleY, "scale Y");
        RequireNear(actual.RotationRadians, expected.RotationRadians, "rotation");
        RequireNear(actual.PivotOffsetX, expected.PivotOffsetX, "pivot X");
        RequireNear(actual.PivotOffsetY, expected.PivotOffsetY, "pivot Y");
        RequireNear(actual.Opacity, expected.Opacity, "opacity");
        if (actual.Color != expected.Color || actual.Depth != expected.Depth ||
            actual.Flags != expected.Flags)
        {
            throw new InvalidOperationException(
                "Headless color/visible/depth/blend state differs from GUI decoration evaluation.");
        }
    }

    private static NativeStaticDecoration[] ReadHeadlessInstances(NativeChartFrameRenderer renderer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object session = typeof(NativeChartFrameRenderer).GetField("_session", flags)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Native chart frame renderer has no active session.");
        return session.GetType().GetField("_staticDecorations", flags)?.GetValue(session)
                   as NativeStaticDecoration[]
               ?? throw new InvalidOperationException(
                   "RED: headless session does not retain the time-evaluated decoration array.");
    }

    private static void VerifyVideoFrames(
        LevelDocument level,
        TimingMap timing,
        double start,
        double duration)
    {
        using var renderer = new NativeChartFrameRenderer(160, 90);
        renderer.Load(level, timing);
        var pipeline = new VideoExportFramePipeline(renderer);
        var plan = new VideoExportPlan(start, start + duration + 0.01, 4, 160, 90);
        var lumas = new List<int>();
        pipeline.RenderAsync(
                plan,
                (frame, rendered, _) =>
                {
                    int center = ((45 * 160) + 80) * 3;
                    lumas.Add(Luma(rendered.Rgb, center));
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        if (lumas.Count < 3 || lumas.Distinct().Count() < 3 || lumas[^1] <= lumas[0])
            throw new InvalidOperationException("Video RenderBorrowed frames did not contain the decoration fade.");
    }

    private static void VerifyNoDecorationAndStaticDecoration(string directory)
    {
        string noDecorationPath = Path.Combine(directory, "none.adofai");
        File.WriteAllText(noDecorationPath, "{\"angleData\":[0],\"settings\":{\"bpm\":60}}");
        using (var renderer = new NativeChartFrameRenderer(80, 45))
        {
            renderer.Load(noDecorationPath);
            RequireEqual(renderer.Render(0), renderer.Render(2),
                "chart without decorations changed solely because time advanced");
        }

        string staticPath = Path.Combine(directory, "static.adofai");
        File.WriteAllText(staticPath, CreateStaticFixture());
        using var staticRenderer = new NativeChartFrameRenderer(80, 45);
        staticRenderer.Load(staticPath);
        RequireEqual(staticRenderer.Render(0), staticRenderer.Render(2),
            "static decoration changed solely because time advanced");
    }

    private static void VerifyRgbaPixels(string directory)
    {
        string controlPath = WriteRgbaFixture(directory, "rgba-control.adofai", null, 100, "None", false);
        string opaquePath = WriteRgbaFixture(directory, "rgba-opaque.adofai", "ff0000ff", 100, "None", false);
        string sixDigitPath = WriteRgbaFixture(directory, "rgba-rgb.adofai", "ff0000", 100, "None", false);
        string alpha33Path = WriteRgbaFixture(directory, "rgba-33.adofai", "ff000033", 100, "None", false);
        string combinedPath = WriteRgbaFixture(directory, "rgba-combined.adofai", "ff000080", 50, "None", false);
        RenderedFrame control = RenderFixture(controlPath);
        RenderedFrame opaque = RenderFixture(opaquePath);
        RenderedFrame sixDigit = RenderFixture(sixDigitPath);
        RenderedFrame alpha33 = RenderFixture(alpha33Path);
        RenderedFrame combined = RenderFixture(combinedPath);

        RequireEqual(opaque, sixDigit, "6-digit RGB no longer matches opaque RGBA pixels");
        long opaqueDifference = PixelDifference(opaque, control);
        long alpha33Difference = PixelDifference(alpha33, control);
        long combinedDifference = PixelDifference(combined, control);
        RequireRatio(alpha33Difference, opaqueDifference, 0.15, 0.25,
            "ffffff33-style alpha was not applied to actual pixels");
        RequireRatio(combinedDifference, opaqueDifference, 0.20, 0.31,
            "color alpha and decoration opacity were not multiplied once");

        foreach (string blend in new[] { "LinearDodge", "Difference" })
        {
            RenderedFrame transparent = RenderFixture(WriteRgbaFixture(
                directory, $"rgba-{blend}-zero.adofai", "ff000000", 100, blend, false));
            RenderedFrame blendOpaque = RenderFixture(WriteRgbaFixture(
                directory, $"rgba-{blend}-opaque.adofai", "ff0000ff", 100, blend, false));
            RenderedFrame blendRgb = RenderFixture(WriteRgbaFixture(
                directory, $"rgba-{blend}-rgb.adofai", "ff0000", 100, blend, false));
            RequireEqual(control, transparent, $"{blend} alpha 0 changed the background");
            RequireEqual(blendOpaque, blendRgb, $"{blend} opaque RGBA changed existing blend output");
        }

        RenderedFrame maskedZero = RenderFixture(WriteRgbaFixture(
            directory, "rgba-mask-zero.adofai", "ff000000", 100, "None", true));
        RenderedFrame maskedHalf = RenderFixture(WriteRgbaFixture(
            directory, "rgba-mask-half.adofai", "ff000080", 100, "None", true));
        RequireEqual(control, maskedZero, "masked alpha 0 decoration changed the background");
        if (PixelDifference(maskedHalf, control) <= 0)
            throw new InvalidOperationException("mask processing discarded target color alpha");
    }

    private static RenderedFrame RenderFixture(string path)
    {
        using var renderer = new NativeChartFrameRenderer(160, 90);
        renderer.Load(path);
        return renderer.Render(0);
    }

    private static string WriteRgbaFixture(
        string directory,
        string fileName,
        string? color,
        double opacity,
        string blend,
        bool masked)
    {
        string colorProperty = color is null ? string.Empty : $", \"color\": \"{color}\"";
        string mask = masked
            ? "{ \"floor\": 0, \"eventType\": \"AddDecoration\", \"decorationImage\": \"fade.bmp\", \"relativeTo\": \"Camera\", \"position\": [0,0], \"scale\": [8000,6000], \"opacity\": 100, \"visible\": true, \"maskingType\": \"Mask\", \"depth\": -25, \"lockScale\": true },"
            : string.Empty;
        string target = color is null
            ? string.Empty
            : $"{{ \"floor\": 0, \"eventType\": \"AddDecoration\", \"decorationImage\": \"fade.bmp\", \"relativeTo\": \"Camera\", \"position\": [0,0], \"scale\": [8000,6000], \"opacity\": {opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"visible\": true, \"depth\": -20, \"blendMode\": \"{blend}\", \"maskingType\": \"{(masked ? "VisibleInsideMask" : "None")}\", \"lockScale\": true{colorProperty} }}";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path,
            $"{{\"angleData\":[0],\"settings\":{{\"bpm\":60,\"zoom\":100}},\"decorations\":[{mask}{target}]}}");
        return path;
    }

    private static void RequireRatio(
        long numerator,
        long denominator,
        double minimum,
        double maximum,
        string message)
    {
        double ratio = denominator > 0 ? (double)numerator / denominator : double.NaN;
        if (!(ratio >= minimum && ratio <= maximum))
            throw new InvalidOperationException($"{message}: ratio={ratio:G6}");
    }

    private static void VerifyTimingParity(LevelDocument level, TimingMap flat)
    {
        TimingMap regular = TimingMapBuilder.Build(level);
        if (regular.Floors.Count != flat.Floors.Count)
            throw new InvalidOperationException("TimingMap builders returned different floor counts.");
        for (int index = 0; index < regular.Floors.Count; index++)
        {
            FloorTiming left = regular.Floors[index];
            FloorTiming right = flat.Floors[index];
            if (Math.Abs(left.EntryTime - right.EntryTime) > 1e-9 ||
                Math.Abs(left.ExitTime - right.ExitTime) > 1e-9)
            {
                throw new InvalidOperationException(
                    $"TimingMap builders differ at floor {index}: " +
                    $"{left.EntryTime:G17}/{right.EntryTime:G17}, " +
                    $"{left.ExitTime:G17}/{right.ExitTime:G17}.");
            }
        }
    }

    private static int Luma(byte[] rgb, int offset) =>
        rgb[offset] + rgb[offset + 1] + rgb[offset + 2];

    private static long PixelDifference(RenderedFrame left, RenderedFrame right)
    {
        if (left.Width != right.Width || left.Height != right.Height || left.Rgb.Length != right.Rgb.Length)
            throw new InvalidOperationException("Cannot compare frames with different dimensions.");
        long difference = 0;
        for (int index = 0; index < left.Rgb.Length; index++)
            difference += Math.Abs(left.Rgb[index] - right.Rgb[index]);
        return difference;
    }

    private static void RequireEqual(RenderedFrame expected, RenderedFrame actual, string message)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height ||
            !expected.Rgb.AsSpan().SequenceEqual(actual.Rgb))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireNear(float actual, float expected, string property)
    {
        if (Math.Abs(actual - expected) > Tolerance)
            throw new InvalidOperationException(
                $"Headless {property} differs from GUI evaluation: {actual:G9}/{expected:G9}.");
    }

    private static string CreateFixture() =>
        """
        {
          "angleData": [0, 0, 0],
          "settings": { "bpm": 60, "pitch": 100, "zoom": 100 },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveDecorations",
              "active": true,
              "duration": 2,
              "ease": "Linear",
              "tag": "fade",
              "positionOffset": [2, -1],
              "pivotOffset": [1, 2],
              "rotation": 90,
              "scale": [8000, 6000],
              "opacity": 100,
              "color": "FF8080",
              "visible": true
            }
          ],
          "decorations": [
            {
              "floor": 0,
              "eventType": "AddDecoration",
              "decorationImage": "fade.bmp",
              "tag": "fade",
              "relativeTo": "Camera",
              "position": [0, 0],
              "pivotOffset": [0, 0],
              "rotation": 0,
              "scale": [8000, 6000],
              "color": "FFFFFF",
              "opacity": 0,
              "visible": true,
              "depth": -25,
              "blendMode": "None",
              "lockScale": true
            }
          ]
        }
        """;

    private static string CreateStaticFixture() =>
        """
        {
          "angleData": [0],
          "settings": { "bpm": 60 },
          "decorations": [
            {
              "floor": 0,
              "eventType": "AddDecoration",
              "decorationImage": "fade.bmp",
              "relativeTo": "Camera",
              "position": [0, 0],
              "scale": [100, 100],
              "opacity": 100
            }
          ]
        }
        """;

    private static string CreateControlFixture() =>
        """
        {
          "angleData": [0, 0, 0],
          "settings": { "bpm": 60, "pitch": 100, "zoom": 100 },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveDecorations",
              "active": true,
              "duration": 2,
              "ease": "Linear",
              "tag": "fade",
              "positionOffset": [2, -1],
              "pivotOffset": [1, 2],
              "rotation": 90,
              "scale": [8000, 6000],
              "opacity": 100,
              "color": "FF8080",
              "visible": true
            }
          ]
        }
        """;

    private static void WriteSolidBitmap(string path)
    {
        const int width = 32;
        const int height = 32;
        const int pixelBytes = width * height * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42);
        writer.Write(14 + 40 + pixelBytes);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (int index = 0; index < width * height; index++)
        {
            writer.Write((byte)0xff);
            writer.Write((byte)0xff);
            writer.Write((byte)0xff);
            writer.Write((byte)0xff);
        }
    }
}
