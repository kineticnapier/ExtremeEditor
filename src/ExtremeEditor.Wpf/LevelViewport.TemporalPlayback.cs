using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private const double PlaybackPastVisibilitySeconds = 0.15;
    private const double PlaybackFutureVisibilitySeconds = 0.50;
    private const float PlaybackCullMarginWorld = 1.5f;

    public bool TemporalPlaybackActive { get; private set; }
    public int TemporalPlaybackCandidateCount { get; private set; }
    public int TemporalPlaybackVisibleFloorCount { get; private set; }
    public int TemporalPlaybackVisibleIconCount { get; private set; }
    public double TemporalPlaybackDrawMilliseconds { get; private set; }

    private void RenderTemporalPlaybackFloors(TimingMap timingMap, double chartTime)
    {
        ArgumentNullException.ThrowIfNull(timingMap);

        long started = Stopwatch.GetTimestamp();
        PlaybackFloorRange range = PlaybackVisibleFloorSelector.Select(
            timingMap,
            chartTime,
            PlaybackPastVisibilitySeconds,
            PlaybackFutureVisibilitySeconds);

        TemporalPlaybackCandidateCount = range.Count;
        TemporalPlaybackVisibleFloorCount = 0;
        TemporalPlaybackVisibleIconCount = 0;

        if (_level is null)
        {
            ClearTemporalPlaybackVisual();
            return;
        }

        WorldRect viewport = GetViewportWorldRect();
        _floorRenderer.BeginFrame(_zoom);

        using DrawingContext drawingContext = _playbackFloorVisual.RenderOpen();
        Vector2[] positions = _level.Positions;
        for (int floor = range.EndExclusive - 1; floor >= range.StartFloor; floor--)
        {
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Vector2 position = positions[floor];
            if (!IntersectsPlaybackViewport(position, viewport, PlaybackCullMarginWorld))
                continue;

            Point center = WorldToScreen(position);
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < _level.Angles.Length &&
                           Math.Abs(_level.Angles[floor] - 999.0) < 0.000001;

            _floorRenderer.DrawFloor(
                drawingContext,
                center,
                _zoom,
                entryAngle,
                exitAngle,
                midSpin,
                selected: false);
            TemporalPlaybackVisibleFloorCount++;
        }

        TemporalPlaybackDrawMilliseconds =
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }

    private static bool IntersectsPlaybackViewport(
        Vector2 center,
        WorldRect viewport,
        float margin) =>
        center.X >= viewport.Left - margin && center.X <= viewport.Right + margin &&
        center.Y >= viewport.Top - margin && center.Y <= viewport.Bottom + margin;

    private void ClearTemporalPlaybackVisual()
    {
        using DrawingContext _ = _playbackFloorVisual.RenderOpen();
        TemporalPlaybackCandidateCount = 0;
        TemporalPlaybackVisibleFloorCount = 0;
        TemporalPlaybackVisibleIconCount = 0;
        TemporalPlaybackDrawMilliseconds = 0.0;
    }
}
