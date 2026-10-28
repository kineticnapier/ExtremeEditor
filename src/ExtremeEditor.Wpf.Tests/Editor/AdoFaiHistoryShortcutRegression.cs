using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class AdoFaiHistoryShortcutRegression
{
    internal static void Run()
    {
        AssertShortcut(Key.Z, control: true, shift: false, alt: false, windows: false, expected: true);
        AssertShortcut(Key.Y, control: true, shift: false, alt: false, windows: false, expected: true);
        AssertShortcut(Key.Z, control: true, shift: true, alt: false, windows: false, expected: true);
        AssertShortcut(Key.Y, control: true, shift: true, alt: false, windows: false, expected: false);
        AssertShortcut(Key.Z, control: false, shift: false, alt: false, windows: false, expected: false);
        AssertShortcut(Key.Z, control: true, shift: false, alt: true, windows: false, expected: false);
        AssertShortcut(Key.Z, control: true, shift: false, alt: false, windows: true, expected: false);

        var viewport = new Border();
        Require(EditorClipboardShortcutRouting.ShouldRouteEditorKeys(null, viewport),
            "native HWND focus must permit editor history shortcuts.");
        Require(!EditorClipboardShortcutRouting.ShouldRouteEditorKeys(new TextBox(), viewport),
            "a text editor must keep its own Undo/Redo shortcuts.");

        LevelDocument document = LevelDocument.CreateSynthetic(4);
        var editor = new EditorSession(document);
        editor.AddAction(1, "SetSpeed");
        var selection = new EditorSelectionState(document.FloorCount);
        selection.SetSelection([]);
        Require(selection.PrimaryFloor < 0 && editor.CanUndo,
            "setup: history must exist while floor selection is empty.");
        editor.Undo();
        Require(document.ActionCount == 0 && editor.CanRedo,
            "Undo must not depend on the selected floor.");
        editor.Redo();
        Require(document.ActionCount == 1,
            "Redo must not depend on the selected floor.");

        string source = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.AdoFaiKeybinds.cs");
        int focusGuard = source.IndexOf(
            "if (!EditorClipboardShortcutRouting.ShouldRouteEditorKeys(", StringComparison.Ordinal);
        int historyGuard = source.IndexOf(
            "if (IsAdoFaiHistoryShortcut(key, control, shift, alt, windows))",
            StringComparison.Ordinal);
        int selectionGuard = source.IndexOf(
            "int primary = _selection.PrimaryFloor;", StringComparison.Ordinal);
        Require(focusGuard >= 0 && historyGuard > focusGuard && selectionGuard > historyGuard,
            "history shortcuts must be routed after focus protection but before selection gating.");
        Require(!source.Contains("case Key.Z:", StringComparison.Ordinal) &&
                !source.Contains("case Key.Y:", StringComparison.Ordinal),
            "the selection-dependent clipboard switch must not handle history shortcuts.");
    }

    private static void AssertShortcut(
        Key key, bool control, bool shift, bool alt, bool windows, bool expected)
    {
        bool actual = MainWindow.IsAdoFaiHistoryShortcut(key, control, shift, alt, windows);
        Require(actual == expected, $"history shortcut mismatch for {key}, Ctrl={control}, Shift={shift}, Alt={alt}, Win={windows}.");
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("#78: source not found: " + relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("#78: " + message);
    }
}
