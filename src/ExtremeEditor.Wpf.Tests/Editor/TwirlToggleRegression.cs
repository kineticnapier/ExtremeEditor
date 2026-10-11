using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class TwirlToggleRegression
{
    internal static void Run()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(5);
        var editor = new EditorSession(level);

        editor.AddAction(2, "Twirl");
        AssertTwirlCount(level, 2, 1, "first add");

        editor.AddAction(2, "Twirl");
        AssertTwirlCount(level, 2, 0, "second add must toggle off");

        editor.Undo();
        AssertTwirlCount(level, 2, 1, "undo toggle-off");

        editor.Redo();
        AssertTwirlCount(level, 2, 0, "redo toggle-off");

        editor.AddAction(2, "Twirl");
        AssertTwirlCount(level, 2, 1, "third add must toggle on");

        editor.Undo();
        AssertTwirlCount(level, 2, 0, "undo toggle-on");
        editor.Redo();
        AssertTwirlCount(level, 2, 1, "redo toggle-on");
    }

    private static void AssertTwirlCount(LevelDocument level, int floor, int expected, string phase)
    {
        int actual = level.ActionStore.Actions.Count(action =>
            action.Floor == floor &&
            string.Equals(action.EventType, "Twirl", StringComparison.Ordinal));
        if (actual != expected)
            throw new InvalidOperationException(
                $"Twirl toggle {phase}: expected {expected} action(s), actual {actual}.");
    }
}
