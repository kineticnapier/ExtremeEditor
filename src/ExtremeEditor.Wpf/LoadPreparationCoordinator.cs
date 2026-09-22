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

        Task<TAudio> audioTask = prepareAudio();
        Task<TTimeline> timelineTask = prepareTimeline();
        Task<TSnapshot> snapshotTask = prepareSnapshot();

        await Task.WhenAll(audioTask, timelineTask, snapshotTask);
        return (await audioTask, await timelineTask, await snapshotTask);
    }
}
