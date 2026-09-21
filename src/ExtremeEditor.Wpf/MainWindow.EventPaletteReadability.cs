using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        if (TryGetNumberedEvent(toolTip, out int digit, out string eventType, out bool decorationPending))
            ApplyReadableEventButton(button, digit, eventType, decorationPending);
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

    private static void ApplyReadableCategoryButton(Button button, string categoryName)
    {
        button.Width = 36;
        button.Height = 31;
        button.Padding = new Thickness(1, 0, 1, 0);
        button.FontFamily = new FontFamily("Segoe UI");
        button.FontSize = 9;
        button.FontWeight = FontWeights.SemiBold;
        button.Content = categoryName switch
        {
            "Gameplay" => "Game",
            "TrackFx" => "Track",
            "DecorationFx" => "Deco",
            "VisualFx" => "Visual",
            "FxModifiers" => "Mods",
            "Jank" => "Jank",
            "Conveniences" => "Util",
            "Favorites" => "Fav",
            _ => categoryName
        };
    }

    private static void ApplyReadableEventButton(
        Button button,
        int digit,
        string eventType,
        bool decorationPending)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keyBadge = new Border
        {
            Background = PaletteKeyBadge,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock
            {
                Text = digit.ToString(),
                Foreground = Brushes.White,
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        content.Children.Add(keyBadge);

        var label = new TextBlock
        {
            Text = SplitPascalCase(eventType),
            Foreground = decorationPending ? PaletteMuted : PaletteText,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 11
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        button.Width = 96;
        button.Height = 38;
        button.Padding = new Thickness(4, 2, 4, 2);
        button.Content = content;
    }

    private static string SplitPascalCase(string value)
    {
        if (value.Length < 2)
            return value;

        var result = new StringBuilder(value.Length + 8);
        result.Append(value[0]);
        for (int i = 1; i < value.Length; i++)
        {
            char current = value[i];
            char previous = value[i - 1];
            bool boundary = char.IsUpper(current) &&
                            (char.IsLower(previous) ||
                             (i + 1 < value.Length && char.IsLower(value[i + 1])));
            if (boundary)
                result.Append(' ');
            result.Append(current);
        }
        return result.ToString();
    }
}
