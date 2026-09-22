using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class ProgressiveLoadReadinessRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        var audioRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timelineRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool editorReady = false;

        Task run = ProgressiveLoadCoordinator.RunAsync(
            prepareEditorCritical: () => Task.CompletedTask,
            prepareAudio: () => audioRelease.Task,
            prepareTimeline: () => timelineRelease.Task,
            markEditorReady: () => editorReady = true);

        await Task.Yield();

        if (!editorReady)
        {
            audioRelease.TrySetResult();
            timelineRelease.TrySetResult();
            await run.ConfigureAwait(false);
            throw new InvalidOperationException(
                "RED: editor-ready must be published as soon as editor-critical preparation completes, " +
                "without waiting for audio or native playback timeline preparation.");
        }

        if (run.IsCompleted)
        {
            throw new InvalidOperationException(
                "Background playback preparation must still be allowed to continue after editor-ready is published.");
        }

        audioRelease.TrySetResult();
        timelineRelease.TrySetResult();
        await run.ConfigureAwait(false);
    }
}
