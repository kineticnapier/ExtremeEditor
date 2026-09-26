using System.Windows;
using System.Windows.Controls;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static void RegisterReadableEventPaletteHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnReadableEventPaletteButtonLoaded),
            handledEventsToo: true);
    }

    private static void OnReadableEventPaletteButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow)
            return;
        if (button.ToolTip is not string toolTip)
            return;

        if (TryGetCategoryName(toolTip, out string categoryName))
        {
            ApplyReadableCategoryButton(button, categoryName);
            return;
        }

        if (TryGetNumberedEvent(toolTip, out _, out _, out _))
            ApplyReadableEventButton(button);
    }

    private static bool TryGetCategoryName(string toolTip, out string categoryName)
    {
        categoryName = string.Empty;
        if (!toolTip.StartsWith("Ctrl+", StringComparison.Ordinal))
            return false;

        int colon = toolTip.IndexOf(':');
        if (colon < 0 || colon + 1 >= toolTip.Length)
            return false;

        categoryName = toolTip[(colon + 1)..].Trim();
        return categoryName.Length > 0;
    }

    private static bool TryGetNumberedEvent(
        string toolTip,
        out int digit,
        out string eventType,
        out bool decorationPending)
    {
        digit = -1;
        eventType = string.Empty;
        decorationPending = false;

        int colon = toolTip.IndexOf(':');
        if (colon != 1 || !char.IsDigit(toolTip[0]))
            return false;

        digit = toolTip[0] - '0';
        string value = toolTip[(colon + 1)..].Trim();
        const string pendingSuffix = " (decoration creation pending)";
        if (value.EndsWith(pendingSuffix, StringComparison.Ordinal))
        {
            value = value[..^pendingSuffix.Length];
            decorationPending = true;
        }

        eventType = value;
        return eventType.Length > 0;
    }

    private static void ApplyReadableCategoryButton(Button button, string _)
    {
        button.Width = 36;
        button.Height = 31;
        button.Padding = new Thickness(1, 0, 1, 0);
    }

    private static void ApplyReadableEventButton(Button button)
    {
        button.Width = 36;
        button.Height = 38;
        button.Padding = new Thickness(3);
    }
}
