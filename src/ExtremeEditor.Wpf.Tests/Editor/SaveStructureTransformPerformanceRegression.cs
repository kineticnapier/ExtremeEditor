using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SaveStructureTransformPerformanceRegression
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-Issue42-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "level.adofai");
        try
        {
            var actions = new JsonArray();
            for (int i = 0; i < 2000; i++)
                actions.Add(new JsonObject { ["floor"] = i % 100 + 1, ["eventType"] = "Flash", ["opacity"] = i });
            var angles = new JsonArray();
            for (int i = 0; i < 120; i++) angles.Add((i * 45) % 360);
            File.WriteAllText(path, new JsonObject
            {
                ["angleData"] = angles,
                ["settings"] = new JsonObject { ["bpm"] = 120 },
                ["actions"] = actions,
                ["decorations"] = new JsonArray()
            }.ToJsonString());

            var editor = new EditorSession(AdoFaiLoader.Load(path).Document);
            for (int i = 0; i < 40; i++) editor.InsertAngle(1, 45);
            AdoFaiEditorSaveService.ResetStructureTransformCount();
            JsonObject saved = AdoFaiEditorSaveService.BuildSaveRoot(editor, path);
            if (AdoFaiEditorSaveService.StructureTransformCount != 0)
                throw new InvalidOperationException("#42: reference-free events replayed every structural edit during save.");
            if (saved["actions"]?.AsArray().Count != 2000)
                throw new InvalidOperationException("#42 optimization changed the saved action count.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
