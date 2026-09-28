using System.Windows;

namespace ExtremeEditor.Wpf;

public sealed partial class GoToFloorDialog : Window
{
    internal GoToFloorDialog(Window? owner, string initialValue)
    {
        InitializeComponent();
        if (owner is not null)
            Owner = owner;
        FloorTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            FloorTextBox.Focus();
            FloorTextBox.SelectAll();
        };
    }

    internal static string? Ask(Window owner, string initialValue)
    {
        var dialog = new GoToFloorDialog(owner, initialValue);
        return dialog.ShowDialog() == true ? dialog.FloorTextBox.Text : null;
    }

    private void GoClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
