using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// Stock ADOFAI (scrCamera.UpdateSize) sets the orthographic size to
// 5 * userSizeMultiplier * zoomSize with zoomSize = zoom / 100, so one world
// unit covers height / (10 * zoomSize) pixels: the board scales with the
// screen height. The realtime viewport already converts the camera timeline
// for its pixel height; headless frames (video export) must do the same, or a
// 1080p export draws tiles and planets at 560/1080 of their stock size while
// camera-relative decorations (sized from the height) stay correct.
internal static class HeadlessCameraZoomScaleRegression
{
    internal static void Run()
    {
        VerifySharedConversion();
        VerifyHeadlessBoardScalesWithHeight();
        Console.WriteLine("PASS: headless camera zoom follows the output height like stock ADOFAI.");
    }

    private static void VerifySharedConversion()
    {
        NativeCameraEvent[] timeline =
        [
            new NativeCameraEvent
            {
                StartTime = 1.0,
                DurationSeconds = 2.0,
                StartX = 3f,
                StartY = 4f,
                TargetX = 5f,
                TargetY = 6f,
                StartRotation = 0.5f,
                TargetRotation = 0.25f,
                StartZoom = 2f,
                TargetZoom = 1f,
                Flags = NativeCameraEvent.FlagApplyMask,
                Ease = NativeCameraEvent.EaseLinear
            }
        ];

        Require(ReferenceEquals(NativeCameraZoomScale.ForViewportHeight(timeline, 560u), timeline),
            "The 560 px reference height must upload the timeline unchanged.");
        Require(NativeCameraZoomScale.ForViewportHeight([], 1080u).Length == 0,
            "An empty timeline must stay empty.");

        NativeCameraEvent[] tall = NativeCameraZoomScale.ForViewportHeight(timeline, 1120u);
        Require(!ReferenceEquals(tall, timeline) && timeline[0].StartZoom == 2f,
            "Scaling must not modify the prepared source timeline.");
        Require(Math.Abs(tall[0].StartZoom - 1f) < 1e-6f && Math.Abs(tall[0].TargetZoom - 0.5f) < 1e-6f,
            "A 1120 px output must halve the view-size multiplier (twice the pixels per unit).");
        NativeCameraEvent expected = timeline[0];
        expected.StartZoom = tall[0].StartZoom;
        expected.TargetZoom = tall[0].TargetZoom;
        Require(tall[0].Equals(expected), "Only the zoom channel may change.");
    }

    private static void VerifyHeadlessBoardScalesWithHeight()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExtremeEditor.HeadlessZoomScale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string chart = Path.Combine(root, "line.adofai");
        try
        {
            File.WriteAllText(chart, """
                {
                  "angleData": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                  "settings": {
                    "version": 15, "bpm": 100, "offset": 0, "pitch": 100, "countdownTicks": 4,
                    "zoom": 200, "backgroundColor": "000000", "showDefaultBGIfNoImage": false,
                    "relativeTo": "Player", "position": [0, 0], "rotation": 0,
                    "trackColor": "ffffff", "trackStyle": "Standard"
                  },
                  "actions": [],
                  "decorations": []
                }
                """);

            double reference = TrackThickness(chart, 996, 560);
            double half = TrackThickness(chart, 960, 540);
            double full = TrackThickness(chart, 1920, 1080);
            Console.WriteLine($"Track thickness at zoom 200: 540p {half:F2}px, 560p {reference:F2}px, 1080p {full:F2}px");

            Require(reference > 4, "The reference frame must show the track.");
            RequireNear(full, reference * 1080.0 / 560.0,
                $"1080p track thickness {full:F2}px must scale with the height from {reference:F2}px at 560p.");
            RequireNear(half, reference * 540.0 / 560.0,
                $"540p track thickness {half:F2}px must scale with the height from {reference:F2}px at 560p.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // Sub-pixel thickness of the straight track, which runs from the screen
    // centre to the right edge: the brightness integrated down a band of
    // columns right of the planets, divided by the brightest row. Anti-aliased
    // edges count fractionally, so the value scales linearly with the board.
    private static double TrackThickness(string chart, int width, int height)
    {
        using var renderer = new NativeChartFrameRenderer(width, height);
        renderer.Load(chart);
        RenderedFrame frame = renderer.Render(0.3);
        int left = frame.Width * 7 / 10;
        int right = frame.Width * 95 / 100;
        double total = 0;
        double peak = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            double row = 0;
            for (int x = left; x < right; x++)
            {
                int i = (y * frame.Width + x) * 3;
                row += (frame.Rgb[i] + frame.Rgb[i + 1] + frame.Rgb[i + 2]) / (3.0 * 255.0);
            }
            row /= right - left;
            total += row;
            peak = Math.Max(peak, row);
        }
        return peak > 0 ? total / peak : 0;
    }

    private static void RequireNear(double actual, double expected, string message) =>
        Require(Math.Abs(actual - expected) <= Math.Max(1.0, expected * 0.04), message);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
