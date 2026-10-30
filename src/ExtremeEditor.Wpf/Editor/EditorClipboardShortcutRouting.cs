using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ExtremeEditor.Wpf;

internal static class EditorClipboardShortcutRouting
{
    internal static bool ShouldRoute(IInputElement? focusedElement) =>
        focusedElement is not TextBoxBase and not PasswordBox;

    internal static bool ShouldRouteEditorKeys(
        IInputElement? focusedElement,
        DependencyObject editorSurface)
    {
        ArgumentNullException.ThrowIfNull(editorSurface);
        if (focusedElement is null || ReferenceEquals(focusedElement, editorSurface))
            return true; // Native child HWND focus is represented as null.
        if (focusedElement is not DependencyObject current)
            return false;

        while (current is not null)
        {
            if (ReferenceEquals(current, editorSurface))
                return true;
            current = current switch
            {
                FrameworkContentElement content => content.Parent,
                System.Windows.Media.Visual => System.Windows.Media.VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return false;
    }
}
