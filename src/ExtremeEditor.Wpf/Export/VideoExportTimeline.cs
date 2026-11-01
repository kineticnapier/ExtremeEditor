namespace ExtremeEditor.Wpf.Export;

// RGB24 export uses a single managed byte[] and a signed 32-bit stride.
// Reject impossible dimensions before starting ffmpeg or rendering audio.
internal static class VideoExportRgb24Buffer
{
    internal static int GetRequiredByteCount(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        long stride = (long)width * 3;
        if (stride > int.MaxValue || height > Array.MaxLength / stride)
            throw new ArgumentOutOfRangeException(
                nameof(width), "RGB24 video frame exceeds the supported byte-array size.");

        return checked((int)(stride * height));
    }
}

internal sealed record VideoExportPlan(
    double StartChartTimeSeconds,
    double EndChartTimeSeconds,
    int FramesPerSecond,
    int Width,
    int Height)
{
    public double ChartRate { get; init; } = 1.0;

    public void Validate()
    {
        if (!double.IsFinite(StartChartTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(StartChartTimeSeconds));
        if (!double.IsFinite(EndChartTimeSeconds) || EndChartTimeSeconds <= StartChartTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(EndChartTimeSeconds));
        if (FramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Height));
        _ = VideoExportRgb24Buffer.GetRequiredByteCount(Width, Height);
        if (!double.IsFinite(ChartRate) || ChartRate <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(ChartRate));
    }
}

internal sealed record VideoExportFrame(
    int Index,
    double OutputTimeSeconds,
    double ChartTimeSeconds,
    double VisualTimeSeconds);

internal static class VideoExportTimeline
{
    internal static int CountFrames(VideoExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        double chartDuration = plan.EndChartTimeSeconds - plan.StartChartTimeSeconds;
        double outputDuration = chartDuration / plan.ChartRate;
        double exactFrameCount = outputDuration * plan.FramesPerSecond;
        if (!double.IsFinite(exactFrameCount) || exactFrameCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(plan), "Video export frame count exceeds the supported range.");

        // Even a range shorter than one frame must emit its starting frame.
        return Math.Max(1, checked((int)Math.Ceiling(exactFrameCount - 1e-12)));
    }

    internal static IEnumerable<VideoExportFrame> Enumerate(VideoExportPlan plan)
    {
        int frameCount = CountFrames(plan);
        for (int index = 0; index < frameCount; index++)
        {
            double outputTime = (double)index / plan.FramesPerSecond;
            double chartTime = plan.StartChartTimeSeconds + outputTime * plan.ChartRate;
            if (!(chartTime < plan.EndChartTimeSeconds))
                throw new InvalidOperationException(
                    "Video export timeline advanced into the exclusive end boundary.");
            yield return new VideoExportFrame(index, outputTime, chartTime, outputTime);
        }
    }

    internal static IReadOnlyList<VideoExportFrame> Build(VideoExportPlan plan)
    {
        return Enumerate(plan).ToArray();
    }
}
