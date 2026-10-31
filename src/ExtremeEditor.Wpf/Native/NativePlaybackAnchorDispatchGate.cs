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

    // A failed native call must not suppress a retry of the same paused
    // or stopped anchor. The gate only retains successfully sent anchors.
    internal bool Dispatch(
        double chartTime, double chartRate, bool active, bool playing,
        Action upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!ShouldSend(chartTime, chartRate, active, playing))
            return false;

        try
        {
            upload();
            return true;
        }
        catch
        {
            Invalidate();
            throw;
        }
    }

    internal void Invalidate() => _hasLast = false;
}
