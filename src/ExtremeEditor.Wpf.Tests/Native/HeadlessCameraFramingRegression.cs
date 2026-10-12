using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class HeadlessCameraFramingRegression
{
    public static void Run()
    {
        string root = FindRepoRoot();
        string headlessPath = Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer_headless.cpp");
        string realtimePath = Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp");

        string headless = File.ReadAllText(headlessPath);
        string realtime = File.ReadAllText(realtimePath);
        var failures = new List<string>();

        if (headless.Contains("float zoom = 28.0f;", StringComparison.Ordinal))
            failures.Add("headless renderer must not use a resolution-independent fixed base zoom");

        if (!headless.Contains("width_", StringComparison.Ordinal) ||
            !headless.Contains("height_", StringComparison.Ordinal))
        {
            failures.Add("headless camera framing must derive base zoom from the render viewport");
        }

        if (!realtime.Contains("zoom_ = 28.0f;", StringComparison.Ordinal))
            failures.Add("realtime renderer reference zoom baseline changed unexpectedly");

        if (!headless.Contains("camera_visual.zoom_multiplier", StringComparison.Ordinal))
            failures.Add("camera event zoom multiplier must still compose on top of normalized base zoom");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Headless camera framing contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.NativeRenderer")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for headless camera framing regression.");
    }
}
