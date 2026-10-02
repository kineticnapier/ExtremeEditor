using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class ChartFrameRendererRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExtremeEditor.HeadlessRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string chartPath = Path.Combine(root, "smoke.adofai");
        string pngPath = Path.Combine(root, "frame.png");

        try
        {
            File.WriteAllText(
                chartPath,
                """
                {
                  "angleData": [0, 90, 180, 270],
                  "settings": {
                    "bpm": 120,
                    "pitch": 100,
                    "countdownTicks": 1
                  },
                  "actions": [
                    { "floor": 1, "eventType": "Twirl" },
                    { "floor": 2, "eventType": "SetSpeed", "speedType": "Multiplier", "bpmMultiplier": 1.5 }
                  ]
                }
                """);

            using IChartFrameRenderer renderer = new NativeChartFrameRenderer();
            renderer.Load(chartPath);

            RenderedFrame first = renderer.Render(0.25);
            RenderedFrame same = renderer.Render(0.25);
            if (first.Width != 320 || first.Height != 180)
                throw new InvalidOperationException("Headless facade did not produce the default 320x180 frame.");
            if (first.Rgb.Length != 320 * 180 * 3)
                throw new InvalidOperationException("Headless facade did not return tightly packed RGB888 data.");
            if (!first.Rgb.AsSpan().SequenceEqual(same.Rgb))
                throw new InvalidOperationException("Repeated facade renders at the same chart time were not deterministic.");

            renderer.Reset();
            RenderedFrame reset = renderer.Render(0.25);
            if (!first.Rgb.AsSpan().SequenceEqual(reset.Rgb))
                throw new InvalidOperationException("Reset changed deterministic frame output.");

            ChartFramePngWriter.Save(first, pngPath);
            byte[] png = File.ReadAllBytes(pngPath);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (png.Length <= signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
                throw new InvalidOperationException("PNG smoke export did not produce a valid PNG signature.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only; the regression result must not depend on temp cleanup.
            }
        }
    }
}
