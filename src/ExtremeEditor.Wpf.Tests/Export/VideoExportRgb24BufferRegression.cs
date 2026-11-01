using ExtremeEditor.Wpf.Export;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportRgb24BufferRegression
{
    internal static void Run()
    {
        const int expected1080pBytes = 1920 * 1080 * 3;
        if (VideoExportRgb24Buffer.GetRequiredByteCount(1920, 1080) != expected1080pBytes)
            throw new InvalidOperationException("1080p RGB24 frame size changed.");

        new VideoExportPlan(0.0, 1.0, 60, 1920, 1080).Validate();
        new VideoExportPlan(0.0, 1.0, 60, 1, 1).Validate();

        RequireRejected(0, 1080);
        RequireRejected(1920, 0);
        RequireRejected(int.MaxValue, 1); // RGB24 stride cannot fit in int.
        RequireRejected(50_000, 50_000); // Valid stride, oversized frame.
        RequireRejected(int.MaxValue, int.MaxValue); // Must not overflow long.
    }

    private static void RequireRejected(int width, int height)
    {
        try
        {
            new VideoExportPlan(0.0, 1.0, 60, width, height).Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"RGB24 export must reject unsupported dimensions {width}x{height}.");
    }
}
