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

        // RED: intentionally sequential. GREEN will start all three operations
        // before awaiting any of them.
        TAudio audio = await prepareAudio();
        TTimeline timeline = await prepareTimeline();
        TSnapshot snapshot = await prepareSnapshot();
        return (audio, timeline, snapshot);
    }
}
