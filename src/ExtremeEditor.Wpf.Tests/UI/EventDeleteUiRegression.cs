using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class EventDeleteUiRegression
{
    public static void Run()
    {
        string xaml = File.ReadAllText(FindSourceFile("src/ExtremeEditor.Wpf/MainWindow.xaml"));
        string editorSource = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.Editor.cs"));
        string propertyEditorSource = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPropertyEditor.cs"));
        string controls = File.ReadAllText(FindSourceFile("src/ExtremeEditor.Wpf/Themes/Controls.xaml"));

        if (xaml.Contains("Content=\"Delete Event\"", StringComparison.Ordinal))
            throw new InvalidOperationException("RED: the legacy top-level Delete Event button must be removed.");
        if (!xaml.Contains("Content=\"Add Event\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The top-level Add Event button must remain available.");

        string[] requiredPropertyHeaderContracts =
        [
            "Command = EditorCommands.DeleteEvent",
            "ToolTip = \"Delete event\"",
            "EditorDeleteEventButtonStyle",
            "Geometry.Parse"
        ];
        foreach (string contract in requiredPropertyHeaderContracts)
        {
            if (!propertyEditorSource.Contains(contract, StringComparison.Ordinal))
                throw new InvalidOperationException($"RED: property header delete control is missing contract: {contract}");
        }

        if (!controls.Contains("x:Key=\"EditorDeleteEventButtonStyle\"", StringComparison.Ordinal))
            throw new InvalidOperationException("RED: the small dark-theme delete button style is missing.");
        if (propertyEditorSource.Contains("🗑", StringComparison.Ordinal) ||
            propertyEditorSource.Contains("🗑️", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Delete Event button must not use a Unicode trash emoji.");
        }

        string[] requiredCommandContracts =
        [
            "AddBinding(EditorCommands.DeleteEvent, ExecuteDeleteEvent, CanExecuteSelectedEvent)",
            "EventList.SelectedItem is not EventListItem item",
            "EventList.SelectedItem is EventListItem"
        ];
        foreach (string contract in requiredCommandContracts)
        {
            if (!editorSource.Contains(contract, StringComparison.Ordinal))
                throw new InvalidOperationException($"Delete Event command/selection guard changed: {contract}");
        }
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Unable to locate {relativePath}.");
    }
}
