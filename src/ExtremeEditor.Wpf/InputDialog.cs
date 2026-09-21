using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExtremeEditor.Wpf;

internal sealed class InputDialog : Window
{
    private readonly TextBox _textBox;

    private InputDialog(Window owner, string title, string prompt, string initialValue)
    {
        Owner = owner;
        Title = title;
        Width = 420;
        Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        _textBox = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 8, 0, 12),
            MinWidth = 330
        };

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(4, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(4, 0, 0, 0) };
        ok.Click += (_, _) => { DialogResult = true; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_textBox);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }

    public static string? Ask(Window owner, string title, string prompt, string initialValue = "")
    {
        var dialog = new InputDialog(owner, title, prompt, initialValue);
        return dialog.ShowDialog() == true ? dialog._textBox.Text : null;
    }

    public static double? AskDouble(Window owner, string title, string prompt, double initialValue = 0.0)
    {
        string? text = Ask(owner, title, prompt, initialValue.ToString("0.###", CultureInfo.InvariantCulture));
        if (text is null)
            return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}
