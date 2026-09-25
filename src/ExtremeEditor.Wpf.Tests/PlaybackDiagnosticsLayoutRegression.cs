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
            if (window.FindName("PlaybackDiagnosticsBar") is not null)
                throw new InvalidOperationException("The always-visible playback diagnostics toolbar must be removed.");

            if (window.FindName("DiagnosticsPanel") is not Border diagnosticsPanel)
                throw new InvalidOperationException("MainWindow.DiagnosticsPanel is missing.");
            if (diagnosticsPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Detailed diagnostics must be collapsed by default.");

            if (window.FindName("DiagnosticsMenuItem") is not MenuItem diagnosticsMenuItem ||
                !diagnosticsMenuItem.IsCheckable)
            {
                throw new InvalidOperationException("A checkable Diagnostics menu item must expose the detailed panel.");
            }

            if (window.FindName("AssetsMenuItem") is not MenuItem assetsMenu ||
                window.FindName("AdoFaiPathText") is not MenuItem assetPath ||
                window.FindName("BrowseAdoFaiButton") is not MenuItem browseAssets ||
                window.FindName("SetupAssetsButton") is not MenuItem setupAssets ||
                !IsDescendantOf(assetPath, assetsMenu) ||
                !IsDescendantOf(browseAssets, assetsMenu) ||
                !IsDescendantOf(setupAssets, assetsMenu))
            {
                throw new InvalidOperationException("Asset path, Browse, and Setup actions must live under Tools > Assets.");
            }

            if (IsDescendantOf<ToolBarTray>(assetPath) ||
                IsDescendantOf<ToolBarTray>(browseAssets) ||
                IsDescendantOf<ToolBarTray>(setupAssets))
            {
                throw new InvalidOperationException("Asset setup controls must not occupy the normal toolbar.");
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

            RequireToolbar(window, "FileHistoryToolBar");
            RequireToolbar(window, "ClipboardViewToolBar");
            RequireToolbar(window, "TransformToolBar");
            RequireToolbar(window, "PlaybackToolBar");

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
                         "Open", "Save", "SaveAs", "Undo", "Redo", "Cut", "Copy", "Paste",
                         "InsertAngle", "RotateLeft", "RotateRight", "FlipHorizontal", "FlipVertical",
                         "Frame", "PlayPause", "Stop"
                     })
            {
                if (!ContainsCommand(window, commandName))
                    throw new InvalidOperationException($"Toolbar command '{commandName}' is missing after UI cleanup.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void RequireToolbar(MainWindow window, string name)
    {
        if (window.FindName(name) is not ToolBar toolbar ||
            toolbar.Parent is not ToolBarTray)
        {
            throw new InvalidOperationException($"Grouped toolbar '{name}' is missing from the top ToolBarTray.");
        }
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
