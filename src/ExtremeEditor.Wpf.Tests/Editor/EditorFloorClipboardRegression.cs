using System.IO;
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
        VerifyPasteAtEnd();
        VerifyProtectedFloorZeroSelection();
        VerifyShortcutRoutingContract();
        VerifyNativeEventPaletteRouting();
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


    private static void VerifyPasteAtEnd()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.Angles = [0, 30, 60];
        level.RebuildGeometry();
        level.ReplaceActions(
        [
            new LevelAction(2, "Twirl", true, null, null, null, null)
            {
                SourceIndex = -2,
                PropertyOverrides = new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = "Twirl",
                    ["marker"] = "tail"
                }
            }
        ]);
        level.ReplaceDecorations(
        [
            new LevelDecoration(3, "AddDecoration")
            {
                SourceIndex = -3,
                Properties = new JsonObject { ["floor"] = 3, ["tag"] = "tail" }
            }
        ]);
        var editor = new EditorSession(level);
        if (!editor.CopyFloors([2, 3]))
            throw new InvalidOperationException("Unable to copy the trailing floor range.");

        // Ctrl+V keeps its existing before-last-floor semantics.
        EditorPasteResult existingPaste = editor.PasteFloors(level.FloorCount)
            ?? throw new InvalidOperationException("Existing paste failed.");
        if (existingPaste.FirstFloor != 3)
            throw new InvalidOperationException("Normal paste unexpectedly changed its insertion position.");
        editor.Undo();

        // The explicit menu command may insert after the final floor.
        EditorPasteResult appended = editor.PasteFloors(level.FloorCount, allowEnd: true)
            ?? throw new InvalidOperationException("Paste at end returned no result.");
        if (appended.FirstFloor != 4 || !appended.Floors.SequenceEqual([4, 5]))
            throw new InvalidOperationException("Paste at end returned the wrong inserted range.");
        if (level.FloorCount != 6 || !level.Angles.SequenceEqual([0.0, 30.0, 60.0, 30.0, 60.0]))
            throw new InvalidOperationException("Paste at end inserted before the last floor.");
        if (!level.ActionStore.Actions.Any(action =>
                action.Floor == 4 && action.EventType == "Twirl" &&
                action.PropertyOverrides?["marker"]?.GetValue<string>() == "tail"))
            throw new InvalidOperationException("Pasted event was not appended with its attributes.");
        if (!level.Decorations.Any(decoration =>
                decoration.Floor == 5 && decoration.Properties["tag"]?.GetValue<string>() == "tail"))
            throw new InvalidOperationException("Pasted decoration was not appended.");

        editor.Undo();
        if (level.FloorCount != 4 || !level.Angles.SequenceEqual([0.0, 30.0, 60.0]))
            throw new InvalidOperationException("Undo did not restore the original final floor.");
        editor.Redo();
        if (level.FloorCount != 6 || !level.Angles.SequenceEqual([0.0, 30.0, 60.0, 30.0, 60.0]))
            throw new InvalidOperationException("Redo did not append the copied range again.");

        if (typeof(MainWindow).GetMethod("ExecutePasteAtEnd",
                BindingFlags.Instance | BindingFlags.NonPublic) is null)
            throw new InvalidOperationException("Paste at end menu handler is missing.");
    }

    private static void VerifyProtectedFloorZeroSelection()
    {
        // The HWND keyboard handler uses this policy for multi-selection deletion.
        MethodInfo? filter = typeof(MainWindow).GetMethod(
            "EditableMultiSelection",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (filter is null)
            throw new InvalidOperationException("Floor-zero deletion policy is missing.");

        int[] editable = filter.Invoke(null, [new[] { 0, 1, 3 }]) as int[]
            ?? throw new InvalidOperationException("Deletion policy returned no floor list.");
        if (!editable.SequenceEqual([1, 3]))
            throw new InvalidOperationException("Floor zero blocked deletion of editable selected floors.");

        int[] protectedOnly = filter.Invoke(null, [new[] { 0 }]) as int[]
            ?? throw new InvalidOperationException("Deletion policy returned no floor list.");
        if (protectedOnly.Length != 0)
            throw new InvalidOperationException("Floor zero must never be deletable.");

        LevelDocument level = LevelDocument.CreateSynthetic(5);
        level.Angles = [0, 30, 60, 90];
        level.RebuildGeometry();
        var editor = new EditorSession(level);

        if (!editor.CopyFloors([0, 1]) || editor.ClipboardFloorCount != 1)
            throw new InvalidOperationException("Copying floors 0 and 1 must copy exactly one editable floor.");

        editor.DeleteFloors(editable);
        if (level.FloorCount != 3 || !level.Angles.SequenceEqual([30.0, 90.0]))
            throw new InvalidOperationException("Multi-delete did not preserve floor zero and remove floors 1 and 3.");

        editor.Undo();
        if (level.FloorCount != 5 || !level.Angles.SequenceEqual([0.0, 30.0, 60.0, 90.0]))
            throw new InvalidOperationException("Undo did not restore floor-zero multi-selection deletion.");
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

        // Event palette shortcuts must use the stricter editor-surface policy.
        // In particular, the event list and settings controls must not add events
        // when their keyboard navigation keys happen to be digits.
        MethodInfo? shouldRouteEditorKeys = routing?.GetMethod(
            "ShouldRouteEditorKeys",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (shouldRouteEditorKeys is null)
            throw new InvalidOperationException("Editor-surface focus routing is unavailable.");

        var surface = new Grid();
        var editorChild = new Button();
        surface.Children.Add(editorChild);
        AssertEditorRoute(shouldRouteEditorKeys, null, surface, true, "native child HWND");
        AssertEditorRoute(shouldRouteEditorKeys, surface, surface, true, "editor viewport");
        AssertEditorRoute(shouldRouteEditorKeys, editorChild, surface, true, "editor descendant");
        AssertEditorRoute(shouldRouteEditorKeys, new ListBoxItem(), surface, false, "event list");
        AssertEditorRoute(shouldRouteEditorKeys, new ComboBox(), surface, false, "settings ComboBox");
        AssertEditorRoute(shouldRouteEditorKeys, new Button(), surface, false, "settings button");
        AssertEditorRoute(shouldRouteEditorKeys, new TextBox(), surface, false, "text editor");

        MethodInfo? bridge = typeof(MainWindow).GetMethod(
            "NativeThreadPreprocessMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (bridge is null)
            throw new InvalidOperationException("Native child HWND keyboard bridge is missing.");
    }

    private static void VerifyNativeEventPaletteRouting()
    {
        var viewport = new Grid();
        var settingsInput = new TextBox();
        var eventList = new ListBoxItem();

        // A WPF input control must retain its number keys. A verified native HWND
        // must receive the palette shortcuts even if WPF retains stale focus.
        if (!MainWindow.ShouldHandlePaletteShortcut(false, false, null, viewport) ||
            MainWindow.ShouldHandlePaletteShortcut(false, false, settingsInput, viewport) ||
            MainWindow.ShouldHandlePaletteShortcut(false, false, eventList, viewport) ||
            !MainWindow.ShouldHandlePaletteShortcut(true, false, settingsInput, viewport) ||
            !MainWindow.ShouldHandlePaletteShortcut(true, false, eventList, viewport) ||
            MainWindow.ShouldHandlePaletteShortcut(true, true, null, viewport) ||
            MainWindow.ShouldHandlePaletteShortcut(false, true, null, viewport))
        {
            throw new InvalidOperationException(
                "#73: native palette routing must preserve WPF focus safety and suppress repeats.");
        }

        // The real native message pump must enter the palette handler, which
        // delegates unhandled keys to the existing ADOFAI editor bindings.
        string native = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.NativeKeyboardBridge.cs");
        string picker = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventQuickPicker.cs");
        if (!native.Contains("HandleExtremeEditorPreviewKeyDown(this, args, fromNativeChild: true)", StringComparison.Ordinal) ||
            !picker.Contains("HandleExtremeEditorPreviewKeyDown(sender, e, fromNativeChild: false)", StringComparison.Ordinal) ||
            !picker.Contains("AdoFaiPreviewKeyDown(sender, e);", StringComparison.Ordinal) ||
            !native.Contains("_nativeKeysDown.TryPress(key, win32Repeat)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "#73: native key messages must pass through the palette exactly once before ADOFAI bindings.");
        }
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("#73: source file not found: " + relativePath);
    }

    private static void AssertRoute(MethodInfo method, IInputElement? focus, bool expected, string label)
    {
        bool actual = (bool)(method.Invoke(null, [focus]) ?? false);
        if (actual != expected)
            throw new InvalidOperationException($"Clipboard routing mismatch for {label}: expected={expected}, actual={actual}.");
    }

    private static void AssertEditorRoute(
        MethodInfo method, IInputElement? focus, DependencyObject editorSurface,
        bool expected, string label)
    {
        bool actual = (bool)(method.Invoke(null, [focus, editorSurface]) ?? false);
        if (actual != expected)
            throw new InvalidOperationException(
                $"Editor palette focus routing mismatch for {label}: expected={expected}, actual={actual}.");
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }
}
