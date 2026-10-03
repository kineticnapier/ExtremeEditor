using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorClipboardSafetyRegression
{
    internal static void Run()
    {
        // Preserve all existing continuous copy/paste, relative ownership,
        // deep-copy, boundary and one-command undo contracts.
        EditorFloorClipboardRegression.Run();

        var failures = new List<string>();
        VerifyBackwardFirstAngleIsRejected(failures);
        VerifyNonContiguousCopyIsRejected(failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Clipboard safety contract failed: " + string.Join("; ", failures) + ".");
        }
    }

    private static void VerifyBackwardFirstAngleIsRejected(List<string> failures)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(7);
        level.Angles = [0.0, 90.0, 180.0, 270.0, 45.0, 135.0];
        level.RebuildGeometry();

        var editor = new EditorSession(level);
        editor.CopyFloors([4]); // First copied direction is 270 degrees.

        int beforeFloorCount = level.FloorCount;
        double[] beforeAngles = level.Angles.ToArray();
        EditorPasteResult? result = editor.PasteFloors(2);

        // Floor 2 was created by 90 degrees, so its backwards direction is 270.
        if (result is not null || level.FloorCount != beforeFloorCount ||
            !level.Angles.SequenceEqual(beforeAngles))
        {
            failures.Add("paste accepted a clipboard whose first absolute angle points backwards");
        }
    }

    private static void VerifyNonContiguousCopyIsRejected(List<string> failures)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(7);
        level.Angles = [0.0, 30.0, 60.0, 90.0, 120.0, 150.0];
        level.RebuildGeometry();

        var editor = new EditorSession(level);
        editor.CopyFloors([2, 4]);

        int beforeFloorCount = level.FloorCount;
        EditorPasteResult? result = editor.PasteFloors(5);
        if (editor.HasClipboard || result is not null || level.FloorCount != beforeFloorCount)
        {
            failures.Add("non-contiguous floor selection was packed into an unsafe clipboard block");
        }
    }
}
