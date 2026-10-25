using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class AsyncSaveGenerationRegression
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-AsyncSaveGeneration-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "level.adofai");
        string saveAsPath = Path.Combine(directory, "saved.adofai");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(path, CreateFixture().ToJsonString());
            var writer = new ControlledWriter();
            var session = new EditorSession(AdoFaiLoader.Load(path).Document, writer.WriteAsync);

            session.InsertAngle(1, 45);
            Task firstSave = session.SaveAsync(saveAsPath);
            writer.FirstEntered.Task.GetAwaiter().GetResult();

            session.InsertAngle(2, 135);
            Task secondSave = session.SaveAsync(saveAsPath);
            if (writer.CallCount != 1)
                throw new InvalidOperationException("Concurrent saves entered the writer instead of serializing.");

            writer.ReleaseFirst.TrySetResult();
            writer.SecondEntered.Task.GetAwaiter().GetResult();
            firstSave.GetAwaiter().GetResult();
            if (!session.IsDirty)
                throw new InvalidOperationException("RED: an edit made during async save was marked as saved.");

            writer.ReleaseSecond.TrySetResult();
            secondSave.GetAwaiter().GetResult();
            if (session.IsDirty)
                throw new InvalidOperationException("The queued save did not save the latest revision.");
            if (writer.MaximumConcurrentWrites != 1)
                throw new InvalidOperationException("Save writer calls overlapped.");
            if (writer.Roots.Count != 2 || AngleCount(writer.Roots[0]) != 3 || AngleCount(writer.Roots[1]) != 4)
                throw new InvalidOperationException("Queued saves did not capture their own edit revisions.");
            if (!string.Equals(session.Document.SourcePath, saveAsPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                throw new InvalidOperationException("Concurrent Save As changed the wrong source file or path.");
            }

            session.InsertAngle(3, 225);
            session.Undo();
            if (session.IsDirty)
                throw new InvalidOperationException("Undo back to the saved revision must be clean.");
            session.Redo();
            if (!session.IsDirty)
                throw new InvalidOperationException("Redo after the saved revision must be dirty.");

            VerifyFailurePreservesFile(saveAsPath);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyFailurePreservesFile(string path)
    {
        string before = File.ReadAllText(path);
        var failed = new EditorSession(
            AdoFaiLoader.Load(path).Document,
            static (_, _, _) => Task.FromException(new IOException("injected save failure")));
        failed.InsertAngle(1, 15);
        try
        {
            failed.SaveAsync(path).GetAwaiter().GetResult();
            throw new InvalidOperationException("Injected save failure unexpectedly succeeded.");
        }
        catch (IOException ex) when (ex.Message == "injected save failure")
        {
        }

        if (!failed.IsDirty || !string.Equals(File.ReadAllText(path), before, StringComparison.Ordinal))
            throw new InvalidOperationException("A failed save changed the dirty state or existing file.");
    }

    private static int AngleCount(JsonObject root) =>
        (root["angleData"] as JsonArray)?.Count
        ?? throw new InvalidOperationException("Save snapshot has no angleData.");

    private static JsonObject CreateFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 90),
        ["settings"] = new JsonObject { ["bpm"] = 120 },
        ["actions"] = new JsonArray(),
        ["decorations"] = new JsonArray()
    };

    private sealed class ControlledWriter
    {
        private int _activeWrites;
        internal readonly TaskCompletionSource FirstEntered = NewSignal();
        internal readonly TaskCompletionSource ReleaseFirst = NewSignal();
        internal readonly TaskCompletionSource SecondEntered = NewSignal();
        internal readonly TaskCompletionSource ReleaseSecond = NewSignal();
        internal List<JsonObject> Roots { get; } = [];
        internal int CallCount { get; private set; }
        internal int MaximumConcurrentWrites { get; private set; }

        internal async Task WriteAsync(JsonObject root, string path, CancellationToken cancellationToken)
        {
            int call = CallCount++;
            int active = Interlocked.Increment(ref _activeWrites);
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, active);
            Roots.Add((JsonObject)root.DeepClone());
            try
            {
                if (call == 0)
                {
                    FirstEntered.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    SecondEntered.TrySetResult();
                    await ReleaseSecond.Task.WaitAsync(cancellationToken);
                }
                await AdoFaiEditorSaveService.WriteAsync(root, path, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
