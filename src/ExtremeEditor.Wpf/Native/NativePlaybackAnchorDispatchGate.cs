namespace ExtremeEditor.Wpf.Native;

/// <summary>
/// Avoid repeated native anchor updates while playback is paused or stopped.
/// Active playback must always forward each clock sample to the renderer.
/// </summary>
internal sealed class NativePlaybackAnchorDispatchGate
{
    private bool _hasLast;
    private (double ChartTime, double ChartRate, bool Active, bool Playing) _last;

    internal bool ShouldSend(double chartTime, double chartRate, bool active, bool playing)
    {
        var current = (chartTime, chartRate, active, playing);
        if (_hasLast && !playing && _last == current)
            return false;

        _last = current;
        _hasLast = true;
        return true;
    }

    // The native renderer may throw after ShouldSend has already cached this
    // anchor. Invalidate on failure so an unchanged paused/stopped frame retries.
    internal void Dispatch(
        double chartTime,
        double chartRate,
        bool active,
        bool playing,
        Action upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!ShouldSend(chartTime, chartRate, active, playing))
            return;

        try
        {
            upload();
        }
        catch
        {
            Invalidate();
            throw;
        }
    }

    internal void Invalidate() => _hasLast = false;
}
