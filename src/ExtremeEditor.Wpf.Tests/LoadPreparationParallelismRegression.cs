using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class LoadPreparationParallelismRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        int started = 0;
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Prepare(int value)
        {
            Interlocked.Increment(ref started);
            return await release.Task.ConfigureAwait(false) + value;
        }

        Task<(int Audio, int Timeline, int Snapshot)> run = LoadPreparationCoordinator.RunAsync(
            () => Prepare(1),
            () => Prepare(2),
            () => Prepare(3));

        await Task.Yield();

        int observedStarted = Volatile.Read(ref started);
        if (observedStarted != 3)
        {
            release.TrySetResult(10);
            await run.ConfigureAwait(false);
            throw new InvalidOperationException(
                "RED: audio, native timeline, and native snapshot preparation must all start before any one preparation completes. " +
                $"Started={observedStarted}/3.");
        }

        release.TrySetResult(10);
        var result = await run.ConfigureAwait(false);
        if (result.Audio != 11 || result.Timeline != 12 || result.Snapshot != 13)
            throw new InvalidOperationException("Parallel load preparation must preserve all preparation results.");
    }
}
