using System.Threading;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Export;

internal sealed class VideoExportFramePipeline
{
    private readonly IChartFrameRenderer _renderer;

    internal VideoExportFramePipeline(IChartFrameRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    internal async ValueTask RenderAsync(
        VideoExportPlan plan,
        Func<VideoExportFrame, RenderedFrame, CancellationToken, ValueTask> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(consumer);

        foreach (VideoExportFrame frame in VideoExportTimeline.Enumerate(plan))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RenderedFrame rendered = _renderer is IBorrowedChartFrameRenderer borrowed
                ? borrowed.RenderBorrowed(
                    frame.ChartTimeSeconds,
                    frame.VisualTimeSeconds)
                : _renderer.Render(
                    frame.ChartTimeSeconds,
                    frame.VisualTimeSeconds);

            cancellationToken.ThrowIfCancellationRequested();
            await consumer(frame, rendered, cancellationToken).ConfigureAwait(false);
        }
    }
}
