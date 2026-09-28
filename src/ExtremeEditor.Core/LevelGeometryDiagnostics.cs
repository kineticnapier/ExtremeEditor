using System.Threading;

namespace ExtremeEditor.Core;

internal static class LevelGeometryDiagnostics
{
    private static int _enabled;
    private static long _rebuildCount;

    internal static void EnableAndReset()
    {
        Interlocked.Exchange(ref _rebuildCount, 0);
        Volatile.Write(ref _enabled, 1);
    }

    internal static long ReadRebuildCount() => Interlocked.Read(ref _rebuildCount);

    internal static void Disable() => Volatile.Write(ref _enabled, 0);

    internal static void RecordRebuild()
    {
        if (Volatile.Read(ref _enabled) != 0)
            Interlocked.Increment(ref _rebuildCount);
    }
}
