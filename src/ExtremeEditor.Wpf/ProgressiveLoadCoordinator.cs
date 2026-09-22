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

        // RED baseline: the current load semantics do not publish an editor-ready
        // state until playback preparation has also completed.
        await Task.WhenAll(editorTask, audioTask, timelineTask).ConfigureAwait(false);
        markEditorReady();
    }
}
