using System.Globalization;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Headless;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!HeadlessRenderOptions.TryParse(args, out HeadlessRenderOptions? options, out bool showHelp, out string? error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine($"error: {error}");
            PrintUsage(Console.Error);
            return 2;
        }

        if (showHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            using var renderer = new NativeChartFrameRenderer(options!.Width, options.Height);
            renderer.Load(options.ChartPath);

            RenderedFrame frame = options.VisualTimeSeconds is double visualTime
                ? renderer.Render(options.SceneTimeSeconds, visualTime)
                : renderer.Render(options.SceneTimeSeconds);

            ChartFramePngWriter.Save(frame, options.OutputPath);

            double effectiveVisualTime = options.VisualTimeSeconds ?? options.SceneTimeSeconds;
            Console.WriteLine(
                FormattableString.Invariant(
                    $"Rendered {frame.Width}x{frame.Height} RGB888 sceneTime={options.SceneTimeSeconds:0.#########} visualTime={effectiveVisualTime:0.#########} -> {Path.GetFullPath(options.OutputPath)}"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("ExtremeEditor.Headless");
        writer.WriteLine();
        writer.WriteLine("Render one deterministic ADOFAI frame to PNG without opening a GUI window.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  ExtremeEditor.Headless --chart <level.adofai> --time <seconds> --output <frame.png> [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --visual-time <seconds>  Visual/unscaled VFX clock. Defaults to --time.");
        writer.WriteLine("  --width <pixels>          Output width. Default: 320.");
        writer.WriteLine("  --height <pixels>         Output height. Default: 180.");
        writer.WriteLine("  --help, -h                Show this help.");
        writer.WriteLine();
        writer.WriteLine("If --visual-time is omitted, sceneTime and visualTime are identical for deterministic DMDOD B0 rendering.");
    }
}
