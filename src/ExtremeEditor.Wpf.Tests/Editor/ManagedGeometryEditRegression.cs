using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class ManagedGeometryEditRegression
{
    public static void Run()
    {
        long geometryChangingRebuilds = MeasureGeometryChangingEdit();
        if (geometryChangingRebuilds != 1)
        {
            throw new InvalidOperationException(
                $"Angle edit must retain one managed geometry rebuild. rebuilds={geometryChangingRebuilds}.");
        }

        SetHitsoundRebuildCounts setHitsound = MeasureSetHitsoundEditAndSemantics();
        if (setHitsound.Edit != 0)
        {
            throw new InvalidOperationException(
                $"SetHitsound property edit triggered managed geometry rebuild. rebuilds={setHitsound.Edit}.");
        }
        if (setHitsound.Undo != 0)
            throw new InvalidOperationException($"SetHitsound undo triggered managed geometry rebuild. rebuilds={setHitsound.Undo}.");
        if (setHitsound.Redo != 0)
            throw new InvalidOperationException($"SetHitsound redo triggered managed geometry rebuild. rebuilds={setHitsound.Redo}.");
    }

    private static long MeasureGeometryChangingEdit()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        var editor = new EditorSession(level);

        LevelGeometryDiagnostics.EnableAndReset();
        try
        {
            editor.Rotate([1], 90.0);
            return LevelGeometryDiagnostics.ReadRebuildCount();
        }
        finally
        {
            LevelGeometryDiagnostics.Disable();
        }
    }

    private static SetHitsoundRebuildCounts MeasureSetHitsoundEditAndSemantics()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        var before = new LevelAction(3, "SetHitsound", true, null, null, null, null)
        {
            SourceIndex = 0,
            GameSound = "Hitsound",
            HitSound = "Kick",
            HitSoundVolumePercent = 100
        };
        level.ReplaceActions([before]);

        var editor = new EditorSession(level);
        // Source identity mapping is a one-time session initialization and has
        // its own current rebuild. Measure only the subsequent property edit.
        editor.CopyFloors([before.Floor]);
        Vector2[] positionsBefore = level.Positions;
        double[] anglesBefore = level.Angles.ToArray();
        WorldRect boundsBefore = level.Bounds;

        LevelGeometryDiagnostics.EnableAndReset();
        long editRebuilds;
        long undoRebuilds;
        long redoRebuilds;
        try
        {
            editor.ReplaceAction(
                before,
                before with
                {
                    HitSound = "Sizzle",
                    HitSoundVolumePercent = 70
                });
            editRebuilds = LevelGeometryDiagnostics.ReadRebuildCount();

            AssertEditedState(level, before, positionsBefore, anglesBefore, boundsBefore);
            VerifySave(editor);

            if (!editor.CanUndo)
                throw new InvalidOperationException("SetHitsound edit must remain undoable.");
            editor.Undo();
            undoRebuilds = LevelGeometryDiagnostics.ReadRebuildCount() - editRebuilds;
            if (!string.Equals(level.ActionStore.Actions.Single().HitSound, "Kick", StringComparison.Ordinal))
                throw new InvalidOperationException("Undo did not restore the original SetHitsound value.");
            AssertGeometryIdentity(level, positionsBefore, anglesBefore, boundsBefore, "Undo");

            editor.Redo();
            redoRebuilds = LevelGeometryDiagnostics.ReadRebuildCount() - editRebuilds - undoRebuilds;
            if (!string.Equals(level.ActionStore.Actions.Single().HitSound, "Sizzle", StringComparison.Ordinal))
                throw new InvalidOperationException("Redo did not restore the edited SetHitsound value.");
            AssertGeometryIdentity(level, positionsBefore, anglesBefore, boundsBefore, "Redo");
        }
        finally
        {
            LevelGeometryDiagnostics.Disable();
        }

        return new SetHitsoundRebuildCounts(editRebuilds, undoRebuilds, redoRebuilds);
    }

    private static void AssertEditedState(
        LevelDocument level,
        LevelAction before,
        Vector2[] positionsBefore,
        double[] anglesBefore,
        WorldRect boundsBefore)
    {
        LevelAction edited = level.ActionStore.Actions.Single();
        if (edited.SourceIndex != before.SourceIndex ||
            !string.Equals(edited.HitSound, "Sizzle", StringComparison.Ordinal) ||
            edited.HitSoundVolumePercent != 70)
        {
            throw new InvalidOperationException("SetHitsound edit did not update Document.Actions or preserve source identity.");
        }

        HitSoundState state = HitSoundTimelineBuilder.Build(level).GetStateAtFloor(before.Floor);
        if (!string.Equals(state.Name, "Sizzle", StringComparison.Ordinal) ||
            Math.Abs(state.Volume - 0.7) > 0.000001)
        {
            throw new InvalidOperationException("SetHitsound edit was not visible to HitSoundTimeline.");
        }

        if (level.ActionCount != 1 ||
            !level.ActionTypeCounts.TryGetValue("SetHitsound", out int count) ||
            count != 1)
        {
            throw new InvalidOperationException("SetHitsound fast path did not refresh ActionStore metadata.");
        }

        AssertGeometryIdentity(level, positionsBefore, anglesBefore, boundsBefore, "Edit");
    }

    private static void AssertGeometryIdentity(
        LevelDocument level,
        Vector2[] positionsBefore,
        double[] anglesBefore,
        WorldRect boundsBefore,
        string operation)
    {
        if (!ReferenceEquals(positionsBefore, level.Positions))
            throw new InvalidOperationException($"{operation} replaced the managed Positions array.");
        if (!anglesBefore.SequenceEqual(level.Angles) || level.Bounds != boundsBefore)
            throw new InvalidOperationException($"{operation} changed Angles or Bounds.");
    }

    private static void VerifySave(EditorSession editor)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-ManagedGeometryEdit-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "saved.adofai");
        Directory.CreateDirectory(directory);
        try
        {
            AdoFaiEditorSaveService.SaveAsync(editor, path).GetAwaiter().GetResult();
            JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("Saved SetHitsound fixture root is invalid.");
            JsonObject action = (root["actions"] as JsonArray)?.OfType<JsonObject>().Single()
                ?? throw new InvalidOperationException("Saved SetHitsound action is missing.");
            if (!string.Equals(action["hitsound"]?.GetValue<string>(), "Sizzle", StringComparison.Ordinal) ||
                action["hitsoundVolume"]?.GetValue<double>() != 70)
            {
                throw new InvalidOperationException("Save did not preserve the edited SetHitsound properties.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private readonly record struct SetHitsoundRebuildCounts(long Edit, long Undo, long Redo);
}
