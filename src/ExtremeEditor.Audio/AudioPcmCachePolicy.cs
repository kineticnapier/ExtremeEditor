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
}
