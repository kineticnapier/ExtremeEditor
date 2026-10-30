using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorMidspinReverseRegression
{
    internal static void Run()
    {
        var failures = new List<string>();

        VerifyDirectionCase([0.0], 1, 180.0, expected: true, "normal reverse", failures);
        VerifyDirectionCase([0.0], 1, 0.0, expected: false, "normal non-reverse", failures);
        VerifyDirectionCase([0.0, 999.0], 2, 0.0, expected: true, "one Midspin", failures);
        VerifyDirectionCase([0.0, 999.0, 999.0], 3, 180.0, expected: true, "two Midspins", failures);
        VerifyDirectionCase([0.0, 999.0, 999.0, 999.0], 4, 0.0, expected: true, "three Midspins", failures);
        VerifyDirectionCase([999.0], 1, 0.0, expected: false, "no prior normal angle", failures);
        VerifyReverseHudIndex([0.0], 1, 3, "regular backward A", failures);
        VerifyReverseHudIndex([90.0], 1, 6, "regular backward S", failures);
        VerifyReverseHudIndex([315.0], 1, 0, "regular backward Q", failures);
        VerifyReverseHudIndex([0.0, 999.0], 2, 4, "one Midspin reverse D", failures);
        VerifyReverseHudIndex([0.0, 999.0, 999.0], 3, 3, "two Midspins reverse A", failures);
        VerifyReverseHudIndex([999.0], 1, -1, "unknown pre-Midspin direction", failures);
        VerifyReverseHudIndex([10.0], 1, -1, "off-grid reverse direction", failures);
        VerifyReverseHudIndex([], 0, -1, "first floor has no reverse button", failures);
        VerifyTwirlDoesNotAffectPathDirection(failures);
        VerifyDeleteRemapAndUndoRedo(failures);
        VerifyDeleteSelectionContract(failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Midspin reverse-input contract failed: " + string.Join("; ", failures) + ".");
        }
    }

    private static void VerifyDirectionCase(
        double[] angles,
        int primary,
        double inputDirection,
        bool expected,
        string label,
        List<string> failures)
    {
        LevelDocument level = CreateLevel(angles);
        bool actual = InvokeBackwardsDecision(level, primary, inputDirection);
        if (actual != expected)
        {
            failures.Add(
                $"{label} decision mismatch (expected={expected}, actual={actual}, " +
                $"angles=[{string.Join(",", angles)}], input={inputDirection})");
        }
    }

    private static void VerifyReverseHudIndex(
        double[] angles,
        int primary,
        int expectedIndex,
        string label,
        List<string> failures)
    {
        Type helper = typeof(MainWindow).Assembly.GetType(
            "ExtremeEditor.Wpf.AdoFaiPathDirection")
            ?? throw new InvalidOperationException("Cannot find AdoFaiPathDirection");
        MethodInfo resolve = helper.GetMethod(
            "GetBackwardHudButtonIndex",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cannot find HUD backwards resolver");
        int actual = (int)(resolve.Invoke(null, [angles, primary]) ?? -99);
        if (actual != expectedIndex)
        {
            failures.Add(
                $"{label}: expected HUD button {expectedIndex}, got {actual}");
        }
    }

    private static void VerifyTwirlDoesNotAffectPathDirection(List<string> failures)
    {
        LevelDocument level = CreateLevel([0.0, 999.0]);
        level.ReplaceActions(
        [
            new LevelAction(1, "Twirl", true, null, null, null, null)
            {
                Kind = LevelActionKind.Twirl,
                SourceIndex = -2
            }
        ]);

        if (!InvokeBackwardsDecision(level, 2, 0.0))
            failures.Add("one-Midspin reverse direction is not preserved when Twirl coexists");
    }

    private static void VerifyDeleteRemapAndUndoRedo(List<string> failures)
    {
        LevelDocument level = CreateLevel([0.0, 999.0, 90.0, 180.0]);
        level.ReplaceActions(
        [
            new LevelAction(2, "Twirl", true, null, null, null, null)
            {
                Kind = LevelActionKind.Twirl,
                SourceIndex = -2
            },
            new LevelAction(3, "Checkpoint", true, null, null, null, null)
            {
                Kind = LevelActionKind.Checkpoint,
                SourceIndex = -3
            }
        ]);
        level.ReplaceDecorations(
        [
            new LevelDecoration(2, "AddDecoration")
            {
                SourceIndex = -4,
                Properties = new JsonObject { ["floor"] = 2, ["tag"] = "removed" }
            },
            new LevelDecoration(3, "AddDecoration")
            {
                SourceIndex = -5,
                Properties = new JsonObject { ["floor"] = 3, ["tag"] = "shifted" }
            }
        ]);

        var editor = new EditorSession(level);
        editor.DeleteFloors([2]);

        bool deletedState =
            level.FloorCount == 4 &&
            level.ActionStore.Actions.All(action => action.SourceIndex != -2) &&
            level.ActionStore.Actions.Any(action => action.SourceIndex == -3 && action.Floor == 2) &&
            level.Decorations.All(decoration => decoration.SourceIndex != -4) &&
            level.Decorations.Any(decoration => decoration.SourceIndex == -5 && decoration.Floor == 2);
        if (!deletedState)
            failures.Add("Midspin deletion does not preserve action/decoration floor remapping");

        editor.Undo();
        bool undoState =
            level.FloorCount == 5 &&
            level.ActionStore.Actions.Any(action => action.SourceIndex == -2 && action.Floor == 2) &&
            level.ActionStore.Actions.Any(action => action.SourceIndex == -3 && action.Floor == 3) &&
            level.Decorations.Any(decoration => decoration.SourceIndex == -4 && decoration.Floor == 2) &&
            level.Decorations.Any(decoration => decoration.SourceIndex == -5 && decoration.Floor == 3);
        if (!undoState)
            failures.Add("undo did not restore the deleted Midspin floor ownership");

        editor.Redo();
        if (level.FloorCount != 4 || level.Angles.Contains(999.0))
            failures.Add("redo did not reapply the Midspin deletion as one command");
    }

    private static void VerifyDeleteSelectionContract(List<string> failures)
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.AdoFaiKeybinds.cs"));
        const string expected = "RefreshAfterAdoFaiMutation(Math.Max(0, primary - 1))";
        if (!source.Contains(expected, StringComparison.Ordinal))
            failures.Add("reverse deletion does not restore primary selection to the preceding floor");
        string nativeActionSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.NativeEditor.cs"));
        if (!nativeActionSource.Contains(
                "ApplyAdoFaiDirection(editor, primary, request.Value)",
                StringComparison.Ordinal))
            failures.Add("HUD click must use keyboard's reverse-direction deletion semantics");
        string viewportSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ExtremeEditor.Wpf", "Native", "NativeLevelViewport.Preparation.cs"));
        if (!viewportSource.Contains("_selectedFloors.Length == 1", StringComparison.Ordinal) ||
            !viewportSource.Contains("SyncBackwardHudButton();", StringComparison.Ordinal))
            failures.Add("HUD deletion color must update for single selection and chart upload");

    }

    private static bool InvokeBackwardsDecision(LevelDocument level, int primary, double direction)
    {
        object window = RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        FieldInfo levelField = typeof(MainWindow).GetField(
            "_level",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow._level field is unavailable.");
        levelField.SetValue(window, level);

        MethodInfo method = typeof(MainWindow).GetMethod(
            "AdoFaiDirectionPointsBackwards",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MainWindow.AdoFaiDirectionPointsBackwards is unavailable.");

        return (bool)(method.Invoke(window, [primary, direction]) ?? false);
    }

    private static LevelDocument CreateLevel(double[] angles)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(angles.Length + 1);
        level.Angles = angles.ToArray();
        level.RebuildGeometry();
        return level;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExtremeEditor.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Unable to locate the repository root from {AppContext.BaseDirectory}.");
    }
}
