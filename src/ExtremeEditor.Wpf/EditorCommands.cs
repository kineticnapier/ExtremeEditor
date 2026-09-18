using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public static class EditorCommands
{
    public static RoutedUICommand Open { get; } = Create("Open", "Open");
    public static RoutedUICommand Frame { get; } = Create("Frame", "Frame");
    public static RoutedUICommand PlayPause { get; } = Create("Play / Pause", "PlayPause");
    public static RoutedUICommand Stop { get; } = Create("Stop", "Stop");
    public static RoutedUICommand SaveAs { get; } = Create("Save As", "SaveAs");
    public static RoutedUICommand RotateLeft { get; } = Create("Rotate Left", "RotateLeft");
    public static RoutedUICommand RotateRight { get; } = Create("Rotate Right", "RotateRight");

    private static RoutedUICommand Create(string text, string name) =>
        new(text, name, typeof(EditorCommands));
}
