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
    /// Converts camera events from the builder's static/world representation into
    /// the runtime reference frames consumed by the native renderer.
    ///
    /// Player events keep using the smooth-follow pivot. Tile events now retain a
    /// floor index and local camera coordinates so the native renderer can add the
    /// current scene floor position every frame after PositionTrack/MoveTrack have
    /// updated it.
    /// </summary>
    internal static void MakePlayerStartsRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        ArgumentNullException.ThrowIfNull(events);

        MakePlayerReferencesRelative(level, timingMap, events);
        MakeTileReferencesRuntime(level, timingMap, events);
    }

    private static void MakePlayerReferencesRelative(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            ref NativeCameraEvent item = ref events[i];
            if (item.StartTime < InitialEventCutoff)
                continue;

            uint playerFlags = item.Flags &
                (NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY);
            if (playerFlags == 0u)
                continue;

            var pose = timingMap.GetPose(level, item.StartTime);
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerX) != 0u)
                item.StartX -= pose.StationaryPlanet.X;
            if ((playerFlags & NativeCameraEvent.FlagTargetPlayerY) != 0u)
                item.StartY -= pose.StationaryPlanet.Y;
        }
    }

    private static void MakeTileReferencesRuntime(
        LevelDocument level,
        TimingMap timingMap,
        NativeCameraEvent[] events)
    {
        if (events.Length == 0 || level.Positions.Length == 0)
            return;

        CameraSourceData metadata = CameraMetadataCache.Get(level);
        CameraEventPresenceData presenceData = CameraEventPresenceReader.Get(level);

        string movement = NormalizeMovement(metadata.InitialRelativeTo);
        if (movement == "Tile")
        {
            ref NativeCameraEvent initial = ref events[0];
            if (initial.StartTime < InitialEventCutoff)
                MakeTileLocal(ref initial, level, 0);
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
            CameraEventPresence presence = presenceData.BySourceIndex.TryGetValue(source.SourceIndex, out CameraEventPresence found)
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
            MakeTileLocal(ref item, level, floor);
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

    private static void MakeTileLocal(ref NativeCameraEvent item, LevelDocument level, int floor)
    {
        if ((uint)floor >= (uint)level.Positions.Length)
            return;

        var origin = level.Positions[floor];
        if ((item.Flags & NativeCameraEvent.FlagApplyX) != 0u)
        {
            item.StartX -= origin.X;
            item.TargetX -= origin.X;
        }
        if ((item.Flags & NativeCameraEvent.FlagApplyY) != 0u)
        {
            item.StartY -= origin.Y;
            item.TargetY -= origin.Y;
        }

        item.ReferenceFloor = floor;
        item.ReferenceFlags |= NativeCameraEvent.FlagReferenceTile;
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
