using System.Threading;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeLevelUpdateDiagnosticCounts(
    long FullLevelReplacementRequests,
    long PrepareLevelCalls,
    long FlatSnapshotBuilds,
    long FloorIconUpdateRequests,
    int LastFloorIconUpdateStartFloor,
    long ActionEditRoutingInvocations);

internal static class NativeLevelUpdateDiagnostics
{
    private static int _enabled;
    private static long _fullLevelReplacementRequests;
    private static long _prepareLevelCalls;
    private static long _flatSnapshotBuilds;
    private static long _floorIconUpdateRequests;
    private static int _lastFloorIconUpdateStartFloor = -1;
    private static long _actionEditRoutingInvocations;

    internal static void EnableAndReset()
    {
        Interlocked.Exchange(ref _fullLevelReplacementRequests, 0);
        Interlocked.Exchange(ref _prepareLevelCalls, 0);
        Interlocked.Exchange(ref _flatSnapshotBuilds, 0);
        Interlocked.Exchange(ref _floorIconUpdateRequests, 0);
        Interlocked.Exchange(ref _lastFloorIconUpdateStartFloor, -1);
        Interlocked.Exchange(ref _actionEditRoutingInvocations, 0);
        Volatile.Write(ref _enabled, 1);
    }

    internal static NativeLevelUpdateDiagnosticCounts Read() => new(
        Interlocked.Read(ref _fullLevelReplacementRequests),
        Interlocked.Read(ref _prepareLevelCalls),
        Interlocked.Read(ref _flatSnapshotBuilds),
        Interlocked.Read(ref _floorIconUpdateRequests),
        Volatile.Read(ref _lastFloorIconUpdateStartFloor),
        Interlocked.Read(ref _actionEditRoutingInvocations));

    internal static void Disable() => Volatile.Write(ref _enabled, 0);

    internal static void RecordFullLevelReplacementRequest()
    {
        if (Volatile.Read(ref _enabled) != 0)
            Interlocked.Increment(ref _fullLevelReplacementRequests);
    }

    internal static void RecordPrepareLevel()
    {
        if (Volatile.Read(ref _enabled) != 0)
            Interlocked.Increment(ref _prepareLevelCalls);
    }

    internal static void RecordFlatSnapshotBuild()
    {
        if (Volatile.Read(ref _enabled) != 0)
            Interlocked.Increment(ref _flatSnapshotBuilds);
    }

    internal static void RecordFloorIconUpdateRequest(int startFloor)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;

        Volatile.Write(ref _lastFloorIconUpdateStartFloor, startFloor);
        Interlocked.Increment(ref _floorIconUpdateRequests);
    }

    internal static void RecordActionEditRoutingInvocation()
    {
        if (Volatile.Read(ref _enabled) != 0)
            Interlocked.Increment(ref _actionEditRoutingInvocations);
    }
}
