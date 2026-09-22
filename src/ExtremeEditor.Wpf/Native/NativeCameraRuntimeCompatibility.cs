using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeCameraRuntimeCompatibility
{
    private const double InitialEventCutoff = -1.0e50;
    private const double TimeEpsilon = 1.0e-7;

    private readonly record struct PendingSource(
        CameraSourceEvent Source,
        CameraEventPresence Presence,
        double StartTime);

    /// <summary>
    /// Matches the camera rig details that depend on runtime scene state when the
    /// source MoveCamera starts. Tile events capture the floor transform once at
    /// StartEffect time, matching ffxCameraPlus; they do not follow later MoveTrack
    /// motion while the camera tween is already running.
    ///
    /// Player starts are already emitted in world space by
    /// FaithfulNativeCameraTimelineBuilder and must not be converted a second time.
    /// </summary>
    internal static void MakePlayerStartsRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(events);

        FreezeTileTargetsAtStart(level, timingMap, events);
    }

    private static void FreezeTileTargetsAtStart(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        if (events.Length == 0 || level.Positions.Length == 0)
            return;

        CameraSourceData metadata = CameraMetadataCache.Get(level);
        CameraEventPresenceData presenceData = CameraEventPresenceReader.Get(level);
        StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
        NativeTrackTransformEvent[] moveTimeline = TrackTransformResolver.BuildMoveTimeline(
            level,
            timingMap,
            staticTransforms);

        string movement = NormalizeMovement(metadata.InitialRelativeTo);
        if (movement == "Tile")
        {
            ref NativeCameraEvent initial = ref events[0];
            if (initial.StartTime < InitialEventCutoff)
            {
                var frozen = TrackTransformPositionSampler.Evaluate(
                    0,
                    double.NegativeInfinity,
                    staticTransforms,
                    moveTimeline);
                var original = level.Positions[0];
                float dx = frozen.X - original.X;
                float dy = frozen.Y - original.Y;
                if ((initial.Flags & NativeCameraEvent.FlagApplyX) != 0u)
                {
                    initial.StartX += dx;
                    initial.TargetX += dx;
                }
                if ((initial.Flags & NativeCameraEvent.FlagApplyY) != 0u)
                {
                    initial.StartY += dy;
                    initial.TargetY += dy;
                }
            }
        }

        var pending = new List<PendingSource>(metadata.Events.Length);
        foreach (CameraSourceEvent source in metadata.Events)
        {
            if (!source.Active || (uint)source.Floor >= (uint)timingMap.Floors.Count)
                continue;

            FloorTiming timing = timingMap.Floors[source.Floor];
            double bpm = timing.Bpm > 0.0 ? timing.Bpm : Math.Max(0.000001, level.InitialBpm);
            double beatSeconds = 60.0 / bpm;
            double startTime = timing.EntryTime + source.AngleOffset / 180.0 * beatSeconds;
            CameraEventPresence presence = presenceData.BySourceIndex.TryGetValue(
                source.SourceIndex,
                out CameraEventPresence found)
                ? found
                : default;
            pending.Add(new PendingSource(source, presence, startTime));
        }

        pending.Sort(static (a, b) =>
        {
            int byTime = a.StartTime.CompareTo(b.StartTime);
            return byTime != 0 ? byTime : a.Source.SourceIndex.CompareTo(b.Source.SourceIndex);
        });

        int outputIndex = events[0].StartTime < InitialEventCutoff ? 1 : 0;
        foreach (PendingSource pendingSource in pending)
        {
            CameraSourceEvent source = pendingSource.Source;
            CameraEventPresence presence = pendingSource.Presence;

            if (presence.RelativeToSpecified)
                movement = NormalizeMovement(source.RelativeTo);

            bool producesCameraEvent =
                presence.PositionSpecified ||
                presence.RelativeToSpecified ||
                source.Rotation is double ||
                source.Zoom is double;
            if (!producesCameraEvent)
                continue;

            int match = FindEventAt(events, outputIndex, pendingSource.StartTime);
            if (match < 0)
                continue;

            outputIndex = match + 1;
            ref NativeCameraEvent item = ref events[match];
            uint appliedAxes = item.Flags & (NativeCameraEvent.FlagApplyX | NativeCameraEvent.FlagApplyY);
            uint playerAxes = item.Flags & (NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY);
            if (movement != "Tile" || appliedAxes == 0u || playerAxes != 0u)
                continue;

            int floor = Math.Clamp(source.Floor, 0, level.Positions.Length - 1);
            var frozen = TrackTransformPositionSampler.Evaluate(
                floor,
                pendingSource.StartTime,
                staticTransforms,
                moveTimeline);
            var original = level.Positions[floor];
            float dx = frozen.X - original.X;
            float dy = frozen.Y - original.Y;

            if ((item.Flags & NativeCameraEvent.FlagApplyX) != 0u)
                item.TargetX += dx;
            if ((item.Flags & NativeCameraEvent.FlagApplyY) != 0u)
                item.TargetY += dy;
        }
    }

    private static int FindEventAt(NativeCameraEvent[] events, int startIndex, double startTime)
    {
        for (int i = Math.Max(0, startIndex); i < events.Length; i++)
        {
            double delta = events[i].StartTime - startTime;
            if (Math.Abs(delta) <= TimeEpsilon)
                return i;
            if (delta > TimeEpsilon)
                return -1;
        }
        return -1;
    }

    private static string NormalizeMovement(string? value) => value switch
    {
        "Tile" => "Tile",
        "Global" => "Global",
        "LastPosition" => "LastPosition",
        "LastPositionNoRotation" => "LastPositionNoRotation",
        _ => "Player"
    };
}
