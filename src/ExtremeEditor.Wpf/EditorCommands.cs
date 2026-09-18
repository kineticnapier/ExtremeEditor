using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public static class EditorCommands
{
    public static RoutedUICommand Open { get; } = Create("Open", "Open", new KeyGesture(Key.O, ModifierKeys.Control));
    public static RoutedUICommand Frame { get; } = Create("Frame", "Frame", new KeyGesture(Key.F));
    public static RoutedUICommand PlayPause { get; } = Create("Play / Pause", "PlayPause", new KeyGesture(Key.Space));
    public static RoutedUICommand Stop { get; } = Create("Stop", "Stop", new KeyGesture(Key.Escape));
    public static RoutedUICommand SaveAs { get; } = Create("Save As", "SaveAs", new KeyGesture(Key.S, ModifierKeys.Control));
    public static RoutedUICommand RotateLeft { get; } = Create("Rotate Left", "RotateLeft", new KeyGesture(Key.OemOpenBrackets));
    public static RoutedUICommand RotateRight { get; } = Create("Rotate Right", "RotateRight", new KeyGesture(Key.OemCloseBrackets));

    private static RoutedUICommand Create(string text, string name, InputGesture gesture) =>
        new(text, name, typeof(EditorCommands), new InputGestureCollection { gesture });
}
