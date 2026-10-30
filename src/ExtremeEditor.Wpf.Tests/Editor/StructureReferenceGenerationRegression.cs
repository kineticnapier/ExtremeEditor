using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class StructureReferenceGenerationRegression
{
    internal static void Run()
    {
        VerifySaveUndoResaveReferences();
        VerifyCrossTabClipboardRevision();
        VerifyAsyncSaveAsRebasesTemplates();
    }

    private static void VerifySaveUndoResaveReferences()
    {
        string directory = NewDirectory("Undo");
        string path = Path.Combine(directory, "level.adofai");
        try
        {
            File.WriteAllText(path, CreateReferenceFixture().ToJsonString());
            var session = new EditorSession(AdoFaiLoader.Load(path).Document);
            session.InsertAngle(2, 45);
            session.SaveAsync(path).GetAwaiter().GetResult();
            Require(ReadReferenceOffset(path) == 3,
                "RED setup: insertion did not move the ThisTile target.");

            session.Undo();
            session.SaveAsync(path).GetAwaiter().GetResult();
            Require(ReadReferenceOffset(path) == 2,
                "RED: saving, undoing an insertion and saving again kept the rebased reference.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyCrossTabClipboardRevision()
    {
        string directory = NewDirectory("Clipboard");
        string sourcePath = Path.Combine(directory, "source.adofai");
        string targetPath = Path.Combine(directory, "target.adofai");
        try
        {
            JsonObject sourceRoot = CreateReferenceFixture();
            sourceRoot["actions"]![0]!["startTile"] = new JsonArray(2, "Start");
            File.WriteAllText(sourcePath, sourceRoot.ToJsonString());
            JsonObject targetRoot = CreateReferenceFixture();
            targetRoot["actions"] = new JsonArray();
            File.WriteAllText(targetPath, targetRoot.ToJsonString());

            var clipboard = new EditorClipboardStore();
            var source = new EditorSession(AdoFaiLoader.Load(sourcePath).Document, clipboardStore: clipboard);
            LevelAction action = source.GetActionsAtFloor(2).Single();
            action.PropertyOverrides = (JsonObject)(source.GetActionSourceTemplate(action.SourceIndex)
                ?? throw new InvalidOperationException("Source action template is missing.")).DeepClone();
            action.PropertyOverridesStructureRevision = source.StructureEdits.Count;
            source.ReplaceAction(source.GetActionsAtFloor(2).Single(), action);
            Require(source.CopyFloors([2]), "Source event was not copied.");

            var target = new EditorSession(AdoFaiLoader.Load(targetPath).Document, clipboardStore: clipboard);
            target.InsertAngle(1, 45);
            Require(target.PasteFloors(4) is not null, "Cross-tab paste was rejected.");
            target.SaveAsync(targetPath).GetAwaiter().GetResult();

            JsonObject saved = Parse(path: targetPath);
            JsonObject pasted = saved["actions"]!.AsArray().OfType<JsonObject>().Single();
            int offset = pasted["startTile"]![0]!.GetValue<int>();
            Require(offset == 2,
                $"RED: destination edits from before paste were replayed on copied references ({offset}).");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyAsyncSaveAsRebasesTemplates()
    {
        string directory = NewDirectory("AsyncSaveAs");
        string sourceDirectory = Path.Combine(directory, "source");
        string targetDirectory = Path.Combine(directory, "target");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "level.adofai");
        string targetPath = Path.Combine(targetDirectory, "level.adofai");
        string assetPath = Path.Combine(sourceDirectory, "event-bg.png");
        try
        {
            File.WriteAllText(assetPath, "asset");
            JsonObject fixture = CreateReferenceFixture();
            fixture["actions"] = new JsonArray(new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "CustomBackground",
                ["active"] = true,
                ["bgImage"] = "event-bg.png"
            });
            File.WriteAllText(sourcePath, fixture.ToJsonString());

            var writer = new DelayedWriter();
            var session = new EditorSession(AdoFaiLoader.Load(sourcePath).Document, writer.WriteAsync);
            Require(session.CopyFloors([1]), "Source mapping was not initialized.");
            Task first = session.SaveAsync(targetPath);
            writer.FirstEntered.Task.GetAwaiter().GetResult();
            session.InsertAngle(1, 45);
            writer.ReleaseFirst.TrySetResult();
            first.GetAwaiter().GetResult();
            session.SaveAsync(targetPath).GetAwaiter().GetResult();

            Require(writer.Roots.Count == 2, "Expected two save snapshots.");
            foreach (JsonObject root in writer.Roots)
            {
                string reference = root["actions"]![0]!["bgImage"]!.GetValue<string>();
                string resolved = Path.GetFullPath(Path.Combine(targetDirectory, reference));
                Require(string.Equals(resolved, assetPath, StringComparison.OrdinalIgnoreCase),
                    $"RED: a later save restored an event asset reference to the old base: {reference}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int ReadReferenceOffset(string path) =>
        Parse(path)["actions"]![0]!["startTile"]![0]!.GetValue<int>();

    private static JsonObject Parse(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidOperationException("Saved file is not an object.");

    private static string NewDirectory(string name)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-StructureReference-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject CreateReferenceFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 30, 60, 90, 120, 150),
        ["settings"] = new JsonObject { ["bpm"] = 120 },
        ["actions"] = new JsonArray(new JsonObject
        {
            ["floor"] = 2,
            ["eventType"] = "MoveTrack",
            ["active"] = true,
            ["startTile"] = new JsonArray(2, "ThisTile")
        }),
        ["decorations"] = new JsonArray()
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class DelayedWriter
    {
        internal TaskCompletionSource FirstEntered { get; } = NewSignal();
        internal TaskCompletionSource ReleaseFirst { get; } = NewSignal();
        internal List<JsonObject> Roots { get; } = [];

        internal async Task WriteAsync(JsonObject root, string path, CancellationToken cancellationToken)
        {
            Roots.Add((JsonObject)root.DeepClone());
            if (Roots.Count == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            await AdoFaiEditorSaveService.WriteAsync(root, path, cancellationToken);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
