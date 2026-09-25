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
    private const float PlaybackRetentionViewportCount = 2f;

    private readonly HashSet<int> _temporalRetainedFloors = new();
    private readonly List<int> _temporalFloorScratch = new();

    public bool TemporalPlaybackActive { get; private set; }
    public int TemporalPlaybackCandidateCount { get; private set; }
    public int TemporalPlaybackRetainedFloorCount => _temporalRetainedFloors.Count;
    public int TemporalPlaybackVisibleFloorCount { get; private set; }
    public int TemporalPlaybackVisibleIconCount { get; private set; }
    public int TemporalPlaybackVisibleActionFloorCount { get; private set; }
    public int TemporalPlaybackLateAdmissionCount { get; private set; }
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
        TemporalPlaybackVisibleActionFloorCount = 0;

        if (_level is null)
        {
            ClearTemporalPlaybackVisual();
            return;
        }

        WorldRect viewport = GetViewportWorldRect();
        WorldRect retentionViewport = GetPlaybackRetentionViewport(timingMap, chartTime, viewport);
        Vector2[] positions = _level.Positions;

        // The temporal range is the admission/search window. During follow
        // playback, retain floors across the camera sweep to the future pose so
        // high-speed movement can preload them before they enter the real viewport.
        for (int floor = range.StartFloor; floor < range.EndExclusive; floor++)
        {
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Vector2 position = positions[floor];
            if (!IntersectsPlaybackViewport(position, retentionViewport, PlaybackCullMarginWorld))
                continue;

            bool newlyAdmitted = _temporalRetainedFloors.Add(floor);
            if (newlyAdmitted && IntersectsPlaybackViewport(position, viewport, PlaybackCullMarginWorld))
                TemporalPlaybackLateAdmissionCount++;
        }

        _temporalFloorScratch.Clear();
        foreach (int floor in _temporalRetainedFloors)
        {
            if ((uint)floor >= (uint)positions.Length ||
                !IntersectsPlaybackViewport(positions[floor], retentionViewport, PlaybackCullMarginWorld))
            {
                _temporalFloorScratch.Add(floor);
            }
        }
        foreach (int floor in _temporalFloorScratch)
            _temporalRetainedFloors.Remove(floor);

        _temporalFloorScratch.Clear();
        foreach (int floor in _temporalRetainedFloors)
        {
            if (IntersectsPlaybackViewport(positions[floor], viewport, PlaybackCullMarginWorld))
                _temporalFloorScratch.Add(floor);
        }
        _temporalFloorScratch.Sort(static (a, b) => b.CompareTo(a));

        _floorRenderer.BeginFrame(_zoom);
        using DrawingContext drawingContext = _playbackFloorVisual.RenderOpen();
        foreach (int floor in _temporalFloorScratch)
        {
            Vector2 position = positions[floor];
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

            if (_level.ActionsByFloor.ContainsKey(floor))
            {
                TemporalPlaybackVisibleActionFloorCount++;
                if (StaticSceneIconsEnabled &&
                    DrawFloorIcon(drawingContext, floor, center, entryAngle, exitAngle, midSpin))
                {
                    TemporalPlaybackVisibleIconCount++;
                }
            }
        }

        TemporalPlaybackDrawMilliseconds =
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }

    private WorldRect GetPlaybackRetentionViewport(
        TimingMap timingMap,
        double chartTime,
        WorldRect viewport)
    {
        if (!FollowPlayer || _level is null)
            return ExpandPlaybackViewport(viewport, PlaybackRetentionViewportCount);

        PlaybackPose futurePose = timingMap.GetPose(
            _level,
            chartTime + PlaybackFutureVisibilitySeconds);
        Vector2 delta = futurePose.StationaryPlanet - _camera;
        WorldRect futureViewport = new(
            viewport.Left + delta.X,
            viewport.Top + delta.Y,
            viewport.Right + delta.X,
            viewport.Bottom + delta.Y);
        WorldRect sweptViewport = new(
            Math.Min(viewport.Left, futureViewport.Left),
            Math.Min(viewport.Top, futureViewport.Top),
            Math.Max(viewport.Right, futureViewport.Right),
            Math.Max(viewport.Bottom, futureViewport.Bottom));

        float marginX = viewport.Width * PlaybackRetentionViewportCount;
        float marginY = viewport.Height * PlaybackRetentionViewportCount;
        return ExpandPlaybackViewport(sweptViewport, marginX, marginY);
    }

    private static WorldRect ExpandPlaybackViewport(WorldRect viewport, float viewportCount)
    {
        float marginX = viewport.Width * viewportCount;
        float marginY = viewport.Height * viewportCount;
        return ExpandPlaybackViewport(viewport, marginX, marginY);
    }

    private static WorldRect ExpandPlaybackViewport(WorldRect viewport, float marginX, float marginY) =>
        new(
            viewport.Left - marginX,
            viewport.Top - marginY,
            viewport.Right + marginX,
            viewport.Bottom + marginY);

    private static bool IntersectsPlaybackViewport(
        Vector2 center,
        WorldRect viewport,
        float margin) =>
        center.X >= viewport.Left - margin && center.X <= viewport.Right + margin &&
        center.Y >= viewport.Top - margin && center.Y <= viewport.Bottom + margin;

    private void ClearTemporalPlaybackVisual()
    {
        using DrawingContext _ = _playbackFloorVisual.RenderOpen();
        _temporalRetainedFloors.Clear();
        _temporalFloorScratch.Clear();
        TemporalPlaybackCandidateCount = 0;
        TemporalPlaybackVisibleFloorCount = 0;
        TemporalPlaybackVisibleIconCount = 0;
        TemporalPlaybackVisibleActionFloorCount = 0;
        TemporalPlaybackLateAdmissionCount = 0;
        TemporalPlaybackDrawMilliseconds = 0.0;
    }
}
