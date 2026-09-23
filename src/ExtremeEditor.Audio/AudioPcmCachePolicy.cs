using System.Runtime.InteropServices;

namespace ExtremeEditor.Audio;

/// <summary>
/// Chooses conservative defaults for progressive hit-sound PCM caching.
/// The cache is disposable, so low-memory machines should never reserve a
/// fixed minimum that competes with the editor, decoder, or native renderer.
/// </summary>
internal static class AudioPcmCachePolicy
{
    private const long MaximumAutoBudgetBytes = 8L * 1024 * 1024 * 1024;

    internal static long CalculateAutoBudgetBytes(long availableMemoryBytes)
    {
        if (availableMemoryBytes <= 0)
            return 0;

        // Keep four fifths of currently available memory outside the PCM cache.
        // Divide first to avoid overflowing when callers provide very large values.
        long budget = availableMemoryBytes / 5;
        return Math.Min(budget, MaximumAutoBudgetBytes);
    }

    internal static long CalculateInitialPrimeFrames(int sampleRate)
    {
        if (sampleRate <= 0)
            return 0;

        return sampleRate / 2L;
    }

    internal static long GetAvailableMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx
                {
                    Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
                };
                if (GlobalMemoryStatusEx(ref status))
                    return status.AvailablePhysical > long.MaxValue
                        ? long.MaxValue
                        : (long)status.AvailablePhysical;
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        long fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Max(0, fallback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
