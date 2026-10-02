using System.Globalization;

namespace ExtremeEditor.Headless;

internal sealed record HeadlessRenderOptions(
    string ChartPath,
    double SceneTimeSeconds,
    double? VisualTimeSeconds,
    string OutputPath,
    int Width,
    int Height)
{
    public const int DefaultWidth = 320;
    public const int DefaultHeight = 180;

    public static bool TryParse(
        string[] args,
        out HeadlessRenderOptions? options,
        out bool showHelp,
        out string? error)
    {
        options = null;
        showHelp = false;
        error = null;

        if (args.Length == 0)
        {
            error = "Missing arguments.";
            return false;
        }

        string? chart = null;
        string? output = null;
        double? sceneTime = null;
        double? visualTime = null;
        int width = DefaultWidth;
        int height = DefaultHeight;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--help" or "-h")
            {
                showHelp = true;
                return true;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {arg}.";
                return false;
            }

            string value = args[++i];
            switch (arg)
            {
                case "--chart":
                    chart = value;
                    break;
                case "--time":
                    if (!TryParseFiniteDouble(value, out double parsedSceneTime))
                    {
                        error = $"Invalid --time value: {value}";
                        return false;
                    }
                    sceneTime = parsedSceneTime;
                    break;
                case "--visual-time":
                    if (!TryParseFiniteDouble(value, out double parsedVisualTime))
                    {
                        error = $"Invalid --visual-time value: {value}";
                        return false;
                    }
                    visualTime = parsedVisualTime;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--width":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
                    {
                        error = $"Invalid --width value: {value}";
                        return false;
                    }
                    break;
                case "--height":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0)
                    {
                        error = $"Invalid --height value: {value}";
                        return false;
                    }
                    break;
                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(chart))
        {
            error = "--chart is required.";
            return false;
        }
        if (sceneTime is null)
        {
            error = "--time is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            error = "--output is required.";
            return false;
        }

        options = new HeadlessRenderOptions(
            chart,
            sceneTime.Value,
            visualTime,
            output,
            width,
            height);
        return true;
    }

    private static bool TryParseFiniteDouble(string text, out double value) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && double.IsFinite(value);
}
