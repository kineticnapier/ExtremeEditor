using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorFloorClipboardRegression
{
    internal static void Run()
    {
        VerifySingleFloorBoundary();
        VerifyRangePasteAndDeepCopy();
        VerifyShortcutRoutingContract();
    }

    private static void VerifySingleFloorBoundary()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        var editor = new EditorSession(level);
        editor.CopyFloors([1]);
        EditorPasteResult boundaryPaste = editor.PasteFloors(0)
            ?? throw new InvalidOperationException("Boundary paste returned no result.");

        if (level.FloorCount != 5)
            throw new InvalidOperationException("Single-floor paste did not insert exactly one floor.");
        if (boundaryPaste.FirstFloor != 1 || !boundaryPaste.Floors.SequenceEqual([1]))
            throw new InvalidOperationException("Floor-zero paste did not clamp safely to the first editable floor.");
        if (editor.UndoName != "Paste floors")
            throw new InvalidOperationException("Paste must be one undo operation.");
        editor.Undo();
        if (level.FloorCount != 4)
            throw new InvalidOperationException("Single-floor paste undo did not restore the document.");
    }

    private static void VerifyRangePasteAndDeepCopy()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(7);
        level.Angles = [0, 30, 60, 90, 120, 150];
        level.RebuildGeometry();
        var properties = new JsonObject
        {
            ["floor"] = 2,
            ["eventType"] = "PositionTrack",
            ["editorOnly"] = true,
            ["unknownClipboardPayload"] = "preserve-me"
        };
        var action = new LevelAction(2, "PositionTrack", true, null, null, null, null)
        {
            SourceIndex = -2,
            PropertyOverrides = properties
        };
        level.ReplaceActions([action]);
        level.ReplaceDecorations(
        [
            new LevelDecoration(3, "AddDecoration")
            {
                SourceIndex = -3,
                Properties = new JsonObject { ["floor"] = 3, ["tag"] = "floor-owned" }
            },
            new LevelDecoration(null, "AddDecoration")
            {
                SourceIndex = -4,
                Properties = new JsonObject { ["tag"] = "global" }
            }
        ]);

        var editor = new EditorSession(level);
        editor.CopyFloors([2, 3]);
        properties["unknownClipboardPayload"] = "mutated-after-copy";
        EditorPasteResult rangePaste = editor.PasteFloors(5)
            ?? throw new InvalidOperationException("Range paste returned no result.");

        if (level.FloorCount != 9)
            throw new InvalidOperationException("Range paste did not insert both copied floors.");
        if (rangePaste.FirstFloor != 5 || !rangePaste.Floors.SequenceEqual([5, 6]))
            throw new InvalidOperationException("Paste result did not expose the new selection range.");
        AssertNear(30, level.Angles[4], "first pasted angle at primary floor");
        AssertNear(60, level.Angles[5], "second pasted angle relative offset");

        LevelAction pasted = level.ActionStore.Actions.SingleOrDefault(candidate => candidate.Floor == 5)
            ?? throw new InvalidOperationException("Pasted action did not use primary floor as its start.");
        if (pasted.SourceIndex == action.SourceIndex)
            throw new InvalidOperationException("Pasted action reused source identity.");
        if (ReferenceEquals(pasted.PropertyOverrides, action.PropertyOverrides))
            throw new InvalidOperationException("Pasted action shares mutable JSON with the source action.");
        if (pasted.PropertyOverrides?["unknownClipboardPayload"]?.GetValue<string>() != "preserve-me")
            throw new InvalidOperationException("Unknown action JSON was not deep-copied at copy time.");

        if (!level.Decorations.Any(decoration => decoration.Floor == 6 &&
                decoration.Properties["tag"]?.GetValue<string>() == "floor-owned"))
        {
            throw new InvalidOperationException("Floor-owned decoration did not preserve its relative floor.");
        }
        if (level.Decorations.Count(decoration => decoration.Floor is null) != 1)
            throw new InvalidOperationException("Global decoration was copied as though it belonged to a floor.");
    }

    private static void VerifyShortcutRoutingContract()
    {
        Type? routing = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.EditorClipboardShortcutRouting");
        MethodInfo? shouldRoute = routing?.GetMethod(
            "ShouldRoute",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (shouldRoute is null)
            throw new InvalidOperationException("Floor clipboard shortcut focus routing is not available.");

        AssertRoute(shouldRoute, null, expected: true, "editor/native viewport focus");
        AssertRoute(shouldRoute, new TextBox(), expected: false, "TextBox focus");
        AssertRoute(shouldRoute, new PasswordBox(), expected: false, "PasswordBox focus");

        MethodInfo? bridge = typeof(MainWindow).GetMethod(
            "NativeThreadPreprocessMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (bridge is null)
            throw new InvalidOperationException("Native child HWND keyboard bridge is missing.");
    }

    private static void AssertRoute(MethodInfo method, IInputElement? focus, bool expected, string label)
    {
        bool actual = (bool)(method.Invoke(null, [focus]) ?? false);
        if (actual != expected)
            throw new InvalidOperationException($"Clipboard routing mismatch for {label}: expected={expected}, actual={actual}.");
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }
}
