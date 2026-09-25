using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackDiagnosticsLayoutRegression
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            MaterializeStartupXaml(window);

            if (window.FindName("PlaybackDiagnosticsBar") is not null)
                throw new InvalidOperationException("The always-visible playback diagnostics toolbar must be removed.");

            if (window.FindName("DiagnosticsPanel") is not Border diagnosticsPanel)
                throw new InvalidOperationException("MainWindow.DiagnosticsPanel is missing.");
            if (diagnosticsPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Detailed diagnostics must be collapsed by default.");

            if (window.FindName("DiagnosticsMenuItem") is not MenuItem diagnosticsMenuItem ||
                !diagnosticsMenuItem.IsCheckable)
            {
                throw new InvalidOperationException("View > Show Diagnostics must expose the detailed panel.");
            }

            if (window.FindName("MainMenu") is not Menu mainMenu)
                throw new InvalidOperationException("The standard MainWindow menu bar is missing.");

            MenuItem fileMenu = RequireTopLevelMenu(window, mainMenu, "FileMenu", "_File");
            MenuItem editMenu = RequireTopLevelMenu(window, mainMenu, "EditMenu", "_Edit");
            MenuItem viewMenu = RequireTopLevelMenu(window, mainMenu, "ViewMenu", "_View");
            MenuItem toolsMenu = RequireTopLevelMenu(window, mainMenu, "ToolsMenu", "_Tools");
            MenuItem helpMenu = RequireTopLevelMenu(window, mainMenu, "HelpMenu", "_Help");

            RequireMenuCommand(fileMenu, EditorCommands.Open);
            RequireMenuCommand(fileMenu, EditorCommands.Save);
            RequireMenuCommand(fileMenu, EditorCommands.SaveAs);
            if (window.FindName("ExitMenuItem") is not MenuItem exitItem ||
                !ReferenceEquals(exitItem.Parent, fileMenu))
            {
                throw new InvalidOperationException("File > Exit is missing.");
            }
            RequireMenuCommand(editMenu, EditorCommands.Undo);
            RequireMenuCommand(editMenu, EditorCommands.Redo);
            RequireMenuCommand(editMenu, EditorCommands.Cut);
            RequireMenuCommand(editMenu, EditorCommands.Copy);
            RequireMenuCommand(editMenu, EditorCommands.Paste);

            if (window.FindName("TransformMenu") is not MenuItem transformMenu ||
                !ReferenceEquals(transformMenu.Parent, editMenu))
            {
                throw new InvalidOperationException("Edit > Transform submenu is missing.");
            }
            foreach (ICommand command in new ICommand[]
                     {
                         EditorCommands.InsertAngle, EditorCommands.RotateLeft, EditorCommands.RotateRight,
                         EditorCommands.FlipHorizontal, EditorCommands.FlipVertical
                     })
            {
                RequireMenuCommand(transformMenu, command);
            }

            RequireMenuCommand(viewMenu, EditorCommands.Frame);
            if (window.FindName("ViewFollowPlayerMenuItem") is not MenuItem { IsCheckable: true } followItem ||
                !ReferenceEquals(followItem.Parent, viewMenu))
                throw new InvalidOperationException("View > Follow Player toggle is missing.");
            if (!ReferenceEquals(diagnosticsMenuItem.Parent, viewMenu))
                throw new InvalidOperationException("Show Diagnostics must live in the View menu.");

            if (window.FindName("AssetsMenu") is not MenuItem assetsMenu ||
                !ReferenceEquals(assetsMenu.Parent, toolsMenu) ||
                window.FindName("AdoFaiPathMenuItem") is not MenuItem ||
                window.FindName("BrowseAdoFaiMenuItem") is not MenuItem ||
                window.FindName("SetupAssetsMenuItem") is not MenuItem)
            {
                throw new InvalidOperationException("Tools > Assets must retain path, Browse, and Setup actions.");
            }
            if (window.FindName("AboutMenuItem") is not MenuItem aboutItem ||
                !ReferenceEquals(aboutItem.Parent, helpMenu))
            {
                throw new InvalidOperationException("Help > About ExtremeEditor is missing.");
            }

            if (window.FindName("PlaybackDiagnosticsText") is not TextBlock diagnosticsText)
                throw new InvalidOperationException("MainWindow.PlaybackDiagnosticsText is missing.");

            if (!IsDescendantOf(diagnosticsText, diagnosticsPanel))
                throw new InvalidOperationException("PlaybackDiagnosticsText must be hosted by the optional diagnostics panel.");

            if (window.FindName("PlaybackFpsText") is not TextBlock fpsText ||
                !IsDescendantOf<StatusBar>(fpsText))
            {
                throw new InvalidOperationException("A concise FPS value must remain available in the status bar.");
            }

            if (!window.PlaybackDiagnosticsSnapshot.Contains("native=", StringComparison.Ordinal))
                throw new InvalidOperationException("Playback diagnostics snapshot must include native renderer state.");

            if (window.FindName("MainToolBar") is not StackPanel toolbar ||
                toolbar.Orientation != Orientation.Horizontal)
            {
                throw new InvalidOperationException("The compact horizontal editor toolbar is missing.");
            }
            if (FindDescendant<ToolBar>(toolbar) is not null ||
                window.FindName("TopToolBarTray") is not null)
            {
                throw new InvalidOperationException("The compact toolbar must not use WPF ToolBar gripper/overflow chrome.");
            }

            foreach (string resourceKey in new[]
                     {
                         "ToolbarBackgroundBrush",
                         "DisabledTextBrush",
                         "SeparatorBrush",
                         "EditorButtonStyle",
                         "EditorToggleButtonStyle"
                     })
            {
                if (!window.Resources.Contains(resourceKey))
                    throw new InvalidOperationException($"MainWindow resource '{resourceKey}' is required for readable dark UI styling.");
            }

            foreach (string commandName in new[]
                     {
                         "Open", "Save", "Frame", "PlayPause", "Stop"
                     })
            {
                if (!ContainsCommand(toolbar, commandName))
                    throw new InvalidOperationException($"Toolbar command '{commandName}' is missing after UI cleanup.");
            }

            foreach (string commandName in new[]
                     {
                         "SaveAs", "Undo", "Redo", "Cut", "Copy", "Paste", "InsertAngle",
                         "RotateLeft", "RotateRight", "FlipHorizontal", "FlipVertical"
                     })
            {
                if (ContainsCommand(toolbar, commandName))
                    throw new InvalidOperationException($"Keyboard-first command '{commandName}' must not remain in the toolbar.");
            }

            RequireKeyBinding(window, Key.Z, ModifierKeys.Control, EditorCommands.Undo);
            RequireKeyBinding(window, Key.Y, ModifierKeys.Control, EditorCommands.Redo);
            RequireKeyBinding(window, Key.C, ModifierKeys.Control, EditorCommands.Copy);
            RequireKeyBinding(window, Key.X, ModifierKeys.Control, EditorCommands.Cut);
            RequireKeyBinding(window, Key.V, ModifierKeys.Control, EditorCommands.Paste);

            MethodInfo initializeBindings = typeof(MainWindow).GetMethod(
                "InitializeEditorCommandBindings",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow.InitializeEditorCommandBindings is missing.");
            initializeBindings.Invoke(window, null);
            foreach (ICommand command in new ICommand[]
                     {
                         EditorCommands.Undo, EditorCommands.Redo,
                         EditorCommands.Cut, EditorCommands.Copy, EditorCommands.Paste,
                         EditorCommands.InsertAngle, EditorCommands.RotateLeft, EditorCommands.RotateRight,
                         EditorCommands.FlipHorizontal, EditorCommands.FlipVertical
                     })
            {
                if (!window.CommandBindings.Cast<CommandBinding>().Any(binding => binding.Command == command))
                    throw new InvalidOperationException($"Command binding '{((RoutedCommand)command).Name}' must remain after toolbar removal.");
            }

            RequireAdoFaiTransformKeyContract();
        }
        finally
        {
            window.Close();
        }
    }

    private static MenuItem RequireTopLevelMenu(MainWindow window, Menu menu, string name, string header)
    {
        if (window.FindName(name) is not MenuItem item ||
            !menu.Items.Contains(item) ||
            !string.Equals(item.Header as string, header, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Top-level menu '{header}' is missing.");
        }

        return item;
    }

    private static void MaterializeStartupXaml(MainWindow window)
    {
        window.ApplyTemplate();
        ApplyControlTemplates(window);
        window.Measure(new Size(1500, 900));
        window.Arrange(new Rect(0, 0, 1500, 900));
        window.UpdateLayout();
    }

    private static void ApplyControlTemplates(DependencyObject parent)
    {
        if (parent is Control control)
            control.ApplyTemplate();

        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DependencyObject dependencyObject)
                ApplyControlTemplates(dependencyObject);
        }
    }

    private static void RequireMenuCommand(MenuItem parent, ICommand command)
    {
        if (!parent.Items.OfType<MenuItem>().Any(item => item.Command == command))
        {
            throw new InvalidOperationException(
                $"Menu '{parent.Header}' is missing command '{((RoutedCommand)command).Name}'.");
        }
    }

    private static void RequireKeyBinding(MainWindow window, Key key, ModifierKeys modifiers, ICommand command)
    {
        bool exists = window.InputBindings
            .OfType<KeyBinding>()
            .Any(binding => binding.Key == key && binding.Modifiers == modifiers && binding.Command == command);
        if (!exists)
            throw new InvalidOperationException($"Keyboard binding '{modifiers}+{key}' is missing.");
    }

    private static void RequireAdoFaiTransformKeyContract()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExtremeEditor.Wpf",
            "MainWindow",
            "MainWindow.AdoFaiKeybinds.cs"));

        foreach (string contract in new[]
                 {
                     "key == Key.Enter",
                     "key == Key.OemComma",
                     "key == Key.OemPeriod",
                     "key == Key.L",
                     "editor.FlipHorizontal(selection)",
                     "editor.FlipVertical(selection)"
                 })
        {
            if (!source.Contains(contract, StringComparison.Ordinal))
                throw new InvalidOperationException($"ADOFAI transform keyboard contract '{contract}' is missing.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Wpf.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }

    private static bool ContainsCommand(DependencyObject parent, string commandName)
    {
        if (parent is ButtonBase { Command: RoutedCommand command } &&
            string.Equals(command.Name, commandName, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DependencyObject dependencyObject &&
                ContainsCommand(dependencyObject, commandName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject expectedAncestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, expectedAncestor))
                return true;
            current = LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsDescendantOf<TAncestor>(DependencyObject child)
        where TAncestor : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is TAncestor)
                return true;
            current = LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        if (parent is T match)
            return match;

        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DependencyObject dependencyObject &&
                FindDescendant<T>(dependencyObject) is T descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
