namespace ExtremeEditor.Wpf;

internal static class LoadPreparationCoordinator
{
    internal static async Task<(TAudio Audio, TTimeline Timeline, TSnapshot Snapshot)> RunAsync<TAudio, TTimeline, TSnapshot>(
        Func<Task<TAudio>> prepareAudio,
        Func<Task<TTimeline>> prepareTimeline,
        Func<Task<TSnapshot>> prepareSnapshot)
    {
        ArgumentNullException.ThrowIfNull(prepareAudio);
        ArgumentNullException.ThrowIfNull(prepareTimeline);
        ArgumentNullException.ThrowIfNull(prepareSnapshot);

        // Start the worker-friendly native preparation first. prepareAudio is
        // allowed to do synchronous UI-thread work before returning its Task, so
        // this ordering lets that work overlap the already-running workers.
        Task<TTimeline> timelineTask = prepareTimeline();
        Task<TSnapshot> snapshotTask = prepareSnapshot();
        Task<TAudio> audioTask = prepareAudio();

        await Task.WhenAll(audioTask, timelineTask, snapshotTask);
        return (await audioTask, await timelineTask, await snapshotTask);
    }
}
