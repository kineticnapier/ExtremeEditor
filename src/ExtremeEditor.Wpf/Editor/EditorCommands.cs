using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public static class EditorCommands
{
    public static RoutedUICommand Open { get; } = Create("Open", "Open");
    public static RoutedUICommand Save { get; } = Create("Save", "Save");
    public static RoutedUICommand SaveAs { get; } = Create("Save As", "SaveAs");
    public static RoutedUICommand Undo { get; } = Create("Undo", "Undo");
    public static RoutedUICommand Redo { get; } = Create("Redo", "Redo");
    public static RoutedUICommand Copy { get; } = Create("Copy", "Copy");
    public static RoutedUICommand Cut { get; } = Create("Cut", "Cut");
    public static RoutedUICommand Paste { get; } = Create("Paste", "Paste");
    public static RoutedUICommand Delete { get; } = Create("Delete", "Delete");
    public static RoutedUICommand InsertAngle { get; } = Create("Insert Angle", "InsertAngle");
    public static RoutedUICommand InsertMidspin { get; } = Create("Insert Midspin", "InsertMidspin");
    public static RoutedUICommand InsertFullTurn { get; } = Create("Insert 360°", "InsertFullTurn");
    public static RoutedUICommand RotateLeft { get; } = Create("Rotate -90°", "RotateLeft");
    public static RoutedUICommand RotateRight { get; } = Create("Rotate +90°", "RotateRight");
    public static RoutedUICommand Rotate180 { get; } = Create("Rotate 180°", "Rotate180");
    public static RoutedUICommand FlipHorizontal { get; } = Create("Flip Horizontal", "FlipHorizontal");
    public static RoutedUICommand FlipVertical { get; } = Create("Flip Vertical", "FlipVertical");
    public static RoutedUICommand AddEvent { get; } = Create("Add Event", "AddEvent");
    public static RoutedUICommand DeleteEvent { get; } = Create("Delete Event", "DeleteEvent");
    public static RoutedUICommand ApplyEvent { get; } = Create("Apply Event", "ApplyEvent");
    public static RoutedUICommand Frame { get; } = Create("Frame", "Frame");
    public static RoutedUICommand PlayPause { get; } = Create("Play / Pause", "PlayPause");
    public static RoutedUICommand Stop { get; } = Create("Stop", "Stop");

    private static RoutedUICommand Create(string text, string name) =>
        new(text, name, typeof(EditorCommands));
}
