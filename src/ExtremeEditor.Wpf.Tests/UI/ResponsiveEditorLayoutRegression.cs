using System.Windows;
using System.Windows.Controls;

namespace ExtremeEditor.Wpf.Tests;

internal static class ResponsiveEditorLayoutRegression
{
    internal static void Run()
    {
        var window = new MainWindow();
        try
        {
            Require(window.MinWidth <= 700, "window minimum width must fit a 1280px screen at 150% DPI");
            Require(window.MinHeight <= 480, "window minimum height must fit a 720px screen at 150% DPI");

            ColumnDefinition settings = FindColumn(window, "SettingsContentColumn");
            ColumnDefinition viewport = FindColumn(window, "EditorViewportColumn");
            ColumnDefinition inspector = FindColumn(window, "EventInspectorColumn");
            Require(settings.Width.IsStar, "settings content must shrink proportionally");
            Require(viewport.Width.IsStar, "editor viewport must receive proportional remaining width");
            Require(inspector.Width.IsStar, "event inspector must shrink proportionally");
            Require(settings.MinWidth + viewport.MinWidth + inspector.MinWidth + 40 <= window.MinWidth,
                "column minimums must fit inside the window minimum width");
        }
        finally
        {
            window.Close();
        }
    }

    private static ColumnDefinition FindColumn(FrameworkElement window, string name) =>
        window.FindName(name) as ColumnDefinition
        ?? throw new InvalidOperationException($"Missing responsive layout column: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("#80: " + message);
    }
}
