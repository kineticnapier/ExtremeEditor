using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackDiagnosticsLayoutRegression
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            if (window.FindName("PlaybackDiagnosticsBar") is not null)
                throw new InvalidOperationException("The always-visible playback diagnostics toolbar must be removed.");

            if (window.FindName("DiagnosticsPanel") is not Border diagnosticsPanel)
                throw new InvalidOperationException("MainWindow.DiagnosticsPanel is missing.");
            if (diagnosticsPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Detailed diagnostics must be collapsed by default.");

            if (window.FindName("DiagnosticsMenuItem") is not ToggleButton diagnosticsMenuItem)
            {
                throw new InvalidOperationException("A Diagnostics toggle must expose the detailed panel.");
            }

            if (window.FindName("ToolsPopup") is not Popup ||
                window.FindName("ToolsPopupPanel") is not Border ||
                window.FindName("AdoFaiPathText") is not TextBlock ||
                window.FindName("BrowseAdoFaiButton") is not Button ||
                window.FindName("SetupAssetsButton") is not Button)
            {
                throw new InvalidOperationException("Asset path, Browse, and Setup actions must live in the compact Tools popup.");
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

            ToolBar toolbar = RequireToolbar(window, "MainToolBar");

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
                         "Open", "Save", "SaveAs", "Frame", "PlayPause", "Stop"
                     })
            {
                if (!ContainsCommand(toolbar, commandName))
                    throw new InvalidOperationException($"Toolbar command '{commandName}' is missing after UI cleanup.");
            }

            foreach (string commandName in new[]
                     {
                         "Undo", "Redo", "Cut", "Copy", "Paste", "InsertAngle",
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

    private static ToolBar RequireToolbar(MainWindow window, string name)
    {
        if (window.FindName(name) is not ToolBar toolbar ||
            toolbar.Parent is not ToolBarTray)
        {
            throw new InvalidOperationException($"Grouped toolbar '{name}' is missing from the top ToolBarTray.");
        }

        return toolbar;
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
}
