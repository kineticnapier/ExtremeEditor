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
        VerifySingleFloorToMultipleSelection();
        VerifyRangePasteAndDeepCopy();
        VerifyRangeReplacementAndRoundTrip();
        VerifyRepeatedRangePaste();
        VerifyPasteAtEnd();
        VerifyProtectedFloorZeroSelection();
        VerifyShortcutRoutingContract();
        VerifyNativeEventPaletteRouting();
    }

    private static void VerifySingleFloorBoundary()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.ReplaceActions(
        [
            new LevelAction(1, "Twirl", true, null, null, null, null)
            {
                SourceIndex = -2,
                PropertyOverrides = new JsonObject
                {
                    ["floor"] = 1,
                    ["eventType"] = "Twirl",
                    ["marker"] = "copied"
                }
            },
            new LevelAction(2, "SetSpeed", true, "Bpm", 180, null, null)
            {
                SourceIndex = -3,
                PropertyOverrides = new JsonObject
                {
                    ["floor"] = 2,
                    ["eventType"] = "SetSpeed",
                    ["marker"] = "replaced"
                }
            }
        ]);
        level.ReplaceDecorations(
        [
            new LevelDecoration(1, "AddDecoration")
            {
                SourceIndex = -4,
                Properties = new JsonObject
                {
                    ["floor"] = 1,
                    ["relativeTo"] = "Tile",
                    ["tag"] = "tile-copy"
                }
            },
            new LevelDecoration(1, "AddDecoration")
            {
                SourceIndex = -5,
                Properties = new JsonObject
                {
                    ["floor"] = 1,
                    ["relativeTo"] = "Camera",
                    ["tag"] = "camera-skip"
                }
            },
            new LevelDecoration(2, "AddDecoration")
            {
                SourceIndex = -6,
                Properties = new JsonObject
                {
                    ["floor"] = 2,
                    ["relativeTo"] = "Tile",
                    ["tag"] = "target-retained"
                }
            }
        ]);
        var editor = new EditorSession(level);
        editor.CopyFloors([1]);
        EditorPasteResult boundaryPaste = editor.PasteFloors(2)
            ?? throw new InvalidOperationException("Single-floor event paste returned no result.");

        if (level.FloorCount != 4)
            throw new InvalidOperationException("Single-floor paste changed the tile count instead of replacing events.");
        if (boundaryPaste.FirstFloor != 2 || !boundaryPaste.Floors.SequenceEqual([2]))
            throw new InvalidOperationException("Single-floor paste did not retain the target selection.");
        LevelAction[] atTarget = level.ActionStore.Actions.Where(action => action.Floor == 2).ToArray();
        if (atTarget.Length != 1 || atTarget[0].EventType != "Twirl" ||
            atTarget[0].PropertyOverrides?["marker"]?.GetValue<string>() != "copied")
        {
            throw new InvalidOperationException("Single-floor paste did not replace target events with copied events.");
        }
        if (!level.Angles.SequenceEqual([0.0, 0.0, 0.0]))
            throw new InvalidOperationException("Single-floor event paste changed path angles.");
        if (!level.Decorations.Any(decoration => decoration.Floor == 2 &&
                decoration.Properties["tag"]?.GetValue<string>() == "target-retained") ||
            !level.Decorations.Any(decoration => decoration.Floor == 2 &&
                decoration.Properties["tag"]?.GetValue<string>() == "tile-copy") ||
            level.Decorations.Any(decoration => decoration.Floor == 2 &&
                decoration.Properties["tag"]?.GetValue<string>() == "camera-skip"))
        {
            throw new InvalidOperationException(
                "Single-floor event paste did not retain target decorations or filter non-Tile source decorations.");
        }
        if (editor.UndoName != "Paste floors")
            throw new InvalidOperationException("Paste must be one undo operation.");
        editor.Undo();
        if (level.FloorCount != 4 ||
            !level.ActionStore.Actions.Any(action => action.Floor == 2 && action.EventType == "SetSpeed"))
        {
            throw new InvalidOperationException("Single-floor paste undo did not restore target events.");
        }
    }

    private static void VerifySingleFloorToMultipleSelection()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(7);
        level.Angles = [0, 30, 60, 90, 120, 150];
        level.RebuildGeometry();
        level.ReplaceActions(
        [
            Action(1, "Twirl", "source"),
            Action(3, "SetSpeed", "replace-3"),
            Action(4, "CustomUntouched", "untouched"),
            Action(5, "SetHitsound", "replace-5")
        ]);
        var editor = new EditorSession(level);
        editor.CopyFloors([1]);
        EditorPasteResult result = editor.PasteFloors([3, 5], primaryFloor: 3)
            ?? throw new InvalidOperationException("Single-floor multi-selection paste was rejected.");

        if (level.FloorCount != 7 || !result.Floors.SequenceEqual([3, 5]) || result.PrimaryFloor != 3)
            throw new InvalidOperationException("Single-floor multi-selection paste changed floors or selection.");
        if (level.ActionStore.Actions.Count(action =>
                (action.Floor == 3 || action.Floor == 5) && action.EventType == "Twirl") != 2 ||
            level.ActionStore.Actions.Any(action =>
                (action.Floor == 3 || action.Floor == 5) && action.EventType != "Twirl") ||
            !level.ActionStore.Actions.Any(action => action.Floor == 4 && action.EventType == "CustomUntouched"))
        {
            throw new InvalidOperationException("Single-floor paste did not replace only selected floor events.");
        }

        editor.Undo();
        if (!level.ActionStore.Actions.Any(action => action.Floor == 3 && action.EventType == "SetSpeed") ||
            !level.ActionStore.Actions.Any(action => action.Floor == 5 && action.EventType == "SetHitsound"))
        {
            throw new InvalidOperationException("Multi-target event paste undo did not restore original events.");
        }
        editor.Redo();
        if (level.ActionStore.Actions.Count(action =>
                (action.Floor == 3 || action.Floor == 5) && action.EventType == "Twirl") != 2)
        {
            throw new InvalidOperationException("Multi-target event paste redo did not restore copied events.");
        }
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
        if (rangePaste.FirstFloor != 6 || !rangePaste.Floors.SequenceEqual([7]))
            throw new InvalidOperationException("Range paste did not insert after the primary floor and select only its last floor.");
        AssertNear(30, level.Angles[5], "first pasted angle after primary floor");
        AssertNear(60, level.Angles[6], "second pasted angle relative offset");

        LevelAction pasted = level.ActionStore.Actions.SingleOrDefault(candidate => candidate.Floor == 6)
            ?? throw new InvalidOperationException("Pasted action did not use primary floor as its start.");
        if (pasted.SourceIndex == action.SourceIndex)
            throw new InvalidOperationException("Pasted action reused source identity.");
        if (ReferenceEquals(pasted.PropertyOverrides, action.PropertyOverrides))
            throw new InvalidOperationException("Pasted action shares mutable JSON with the source action.");
        if (pasted.PropertyOverrides?["unknownClipboardPayload"]?.GetValue<string>() != "preserve-me")
            throw new InvalidOperationException("Unknown action JSON was not deep-copied at copy time.");

        if (!level.Decorations.Any(decoration => decoration.Floor == 7 &&
                decoration.Properties["tag"]?.GetValue<string>() == "floor-owned"))
        {
            throw new InvalidOperationException("Floor-owned decoration did not preserve its relative floor.");
        }
        if (level.Decorations.Count(decoration => decoration.Floor is null) != 1)
            throw new InvalidOperationException("Global decoration was copied as though it belonged to a floor.");
    }

    private static void VerifyRangeReplacementAndRoundTrip()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-PasteRange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            LevelDocument level = LevelDocument.CreateSynthetic(10);
            level.Angles = [0, 30, 999, 60, 90, 120, 150, 180, 210];
            level.RebuildGeometry();
            level.ReplaceActions(
            [
                Action(2, "PositionTrack", "source-first"),
                Action(3, "Twirl", "source-twirl"),
                Action(5, "SetSpeed", "remove-5"),
                Action(6, "SetHitsound", "remove-6"),
                Action(7, "Flash", "remove-7"),
                Action(8, "CustomAfter", "shift-after")
            ]);
            var editor = new EditorSession(level);
            editor.CopyFloors([2, 3]);

            int beforeCount = level.FloorCount;
            double[] beforeAngles = level.Angles.ToArray();
            EditorPasteResult result = editor.PasteFloors([5, 6, 7], primaryFloor: 7)
                ?? throw new InvalidOperationException("Range replacement was rejected.");
            if (level.FloorCount != beforeCount - 1 || result.PrimaryFloor != 6 ||
                !result.Floors.SequenceEqual([6]))
            {
                throw new InvalidOperationException("Range replacement count or final-floor selection is incorrect.");
            }
            if (editor.UndoName != "Paste floors")
                throw new InvalidOperationException("Range delete/insert was not grouped into one undo operation.");
            if (level.Angles[4] != 30 || level.Angles[5] != 999)
                throw new InvalidOperationException("Range replacement did not preserve copied angle/Midspin order.");
            if (!level.ActionStore.Actions.Any(action => action.Floor == 5 && action.EventType == "PositionTrack") ||
                !level.ActionStore.Actions.Any(action => action.Floor == 6 && action.EventType == "Twirl") ||
                !level.ActionStore.Actions.Any(action => action.Floor == 7 && action.EventType == "CustomAfter") ||
                level.ActionStore.Actions.Any(action =>
                    action.PropertyOverrides?["marker"]?.GetValue<string>()?.StartsWith("remove-") == true))
            {
                throw new InvalidOperationException("Range replacement did not replace/offset events correctly.");
            }

            editor.Undo();
            if (level.FloorCount != beforeCount || !level.Angles.SequenceEqual(beforeAngles) ||
                !level.ActionStore.Actions.Any(action => action.Floor == 7 && action.EventType == "Flash"))
            {
                throw new InvalidOperationException("Range replacement undo did not restore floors and events.");
            }
            editor.Redo();
            if (level.FloorCount != beforeCount - 1 ||
                !level.ActionStore.Actions.Any(action => action.Floor == 6 && action.EventType == "Twirl"))
            {
                throw new InvalidOperationException("Range replacement redo did not restore pasted floors.");
            }

            string saved = Path.Combine(directory, "saved.adofai");
            editor.SaveAsync(saved).GetAwaiter().GetResult();
            LevelDocument reloaded = AdoFaiLoader.Load(saved).Document;
            if (!reloaded.Angles.SequenceEqual(level.Angles) ||
                !reloaded.ActionStore.Actions.Any(action => action.Floor == 6 && action.EventType == "Twirl") ||
                !reloaded.ActionStore.Actions.Any(action => action.Floor == 7 && action.EventType == "CustomAfter"))
            {
                throw new InvalidOperationException("Saved range replacement did not reload with stable floors/events.");
            }

            LevelDocument rejectedLevel = LevelDocument.CreateSynthetic(7);
            var rejected = new EditorSession(rejectedLevel);
            rejected.CopyFloors([2, 3]);
            double[] rejectedAngles = rejectedLevel.Angles.ToArray();
            if (rejected.PasteFloors([0, 1], primaryFloor: 1) is not null ||
                !rejectedLevel.Angles.SequenceEqual(rejectedAngles))
            {
                throw new InvalidOperationException("Range replacement containing floor zero was not rejected atomically.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyRepeatedRangePaste()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        level.Angles = [0, 30, 60, 90, 120];
        level.RebuildGeometry();
        var editor = new EditorSession(level);
        editor.CopyFloors([1, 2]);

        int selected = 4;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            EditorPasteResult result = editor.PasteFloors([selected], selected)
                ?? throw new InvalidOperationException($"Repeated paste {iteration + 1} was rejected.");
            int expectedLast = 6 + iteration * 2;
            if (result.PrimaryFloor != expectedLast || !result.Floors.SequenceEqual([expectedLast]))
                throw new InvalidOperationException("Repeated paste did not select only the last inserted floor.");
            selected = result.PrimaryFloor;
        }

        if (!level.Angles.Skip(4).SequenceEqual([0.0, 30.0, 0.0, 30.0, 0.0, 30.0, 120.0]))
            throw new InvalidOperationException("Three repeated pastes did not extend forward in clipboard order.");
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

        // Normal paste inserts after the selected floor.
        EditorPasteResult existingPaste = editor.PasteFloors(2)
            ?? throw new InvalidOperationException("Existing paste failed.");
        if (existingPaste.FirstFloor != 3)
            throw new InvalidOperationException("Normal paste did not insert after the selected floor.");
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

    private static LevelAction Action(int floor, string eventType, string marker) =>
        new(floor, eventType, true, null, null, null, null)
        {
            SourceIndex = -1000 - floor,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = eventType,
                ["marker"] = marker
            }
        };

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
