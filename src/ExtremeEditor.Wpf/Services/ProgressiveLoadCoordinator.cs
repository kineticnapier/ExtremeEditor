namespace ExtremeEditor.Wpf;

internal static class ProgressiveLoadCoordinator
{
    internal static async Task RunAsync(
        Func<Task> prepareEditorCritical,
        Func<Task> prepareAudio,
        Func<Task> prepareTimeline,
        Action markEditorReady)
    {
        ArgumentNullException.ThrowIfNull(prepareEditorCritical);
        ArgumentNullException.ThrowIfNull(prepareAudio);
        ArgumentNullException.ThrowIfNull(prepareTimeline);
        ArgumentNullException.ThrowIfNull(markEditorReady);

        Task editorTask = prepareEditorCritical();
        Task audioTask = prepareAudio();
        Task timelineTask = prepareTimeline();

        await editorTask.ConfigureAwait(false);
        markEditorReady();

        await Task.WhenAll(audioTask, timelineTask).ConfigureAwait(false);
    }
}
