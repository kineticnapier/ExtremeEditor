using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ExtremeEditor.Wpf;

internal static class EditorClipboardShortcutRouting
{
    internal static bool ShouldRoute(IInputElement? focusedElement) =>
        focusedElement is not TextBoxBase and not PasswordBox;
}
