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

        // Find the first frame outside the exclusive chart-time range.
        // A fixed epsilon can discard a valid frame just past a boundary.
        // Use the same floating-point expression as Enumerate to avoid
        // producing a frame that the enumerator subsequently rejects.
        if (ChartTimeAtFrame(plan, int.MaxValue) < plan.EndChartTimeSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(plan), "Video export frame count exceeds the supported range.");

        int firstOutside = 0;
        int upperBound = int.MaxValue;
        while (firstOutside < upperBound)
        {
            int middle = firstOutside + (upperBound - firstOutside) / 2;
            if (ChartTimeAtFrame(plan, middle) < plan.EndChartTimeSeconds)
                firstOutside = middle + 1;
            else
                upperBound = middle;
        }

        // A positive sub-frame range must still contain its starting frame.
        return Math.Max(1, firstOutside);
    }

    private static double ChartTimeAtFrame(VideoExportPlan plan, int index) =>
        plan.StartChartTimeSeconds +
        ((double)index / plan.FramesPerSecond) * plan.ChartRate;

    internal static IEnumerable<VideoExportFrame> Enumerate(VideoExportPlan plan)
    {
        int frameCount = CountFrames(plan);
        for (int index = 0; index < frameCount; index++)
        {
            double outputTime = (double)index / plan.FramesPerSecond;
            double chartTime = ChartTimeAtFrame(plan, index);
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
