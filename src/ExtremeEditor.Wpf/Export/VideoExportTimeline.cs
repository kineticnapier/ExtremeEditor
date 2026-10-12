namespace ExtremeEditor.Wpf.Export;

internal sealed record VideoExportPlan(
    double StartChartTimeSeconds,
    double EndChartTimeSeconds,
    int FramesPerSecond,
    int Width,
    int Height)
{
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
    }
}

internal sealed record VideoExportFrame(
    int Index,
    double OutputTimeSeconds,
    double ChartTimeSeconds,
    double VisualTimeSeconds);

internal static class VideoExportTimeline
{
    internal static IReadOnlyList<VideoExportFrame> Build(VideoExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();

        double duration = plan.EndChartTimeSeconds - plan.StartChartTimeSeconds;
        double exactFrameCount = duration * plan.FramesPerSecond;
        int frameCount = checked((int)Math.Ceiling(exactFrameCount - 1e-12));

        var frames = new VideoExportFrame[frameCount];
        for (int index = 0; index < frameCount; index++)
        {
            double outputTime = (double)index / plan.FramesPerSecond;
            double chartTime = plan.StartChartTimeSeconds + outputTime;

            if (!(chartTime < plan.EndChartTimeSeconds))
                throw new InvalidOperationException(
                    "Video export timeline advanced into the exclusive end boundary.");

            frames[index] = new VideoExportFrame(
                index,
                outputTime,
                chartTime,
                outputTime);
        }

        return frames;
    }
}
