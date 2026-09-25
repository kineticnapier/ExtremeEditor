using System.Windows;
using System.Windows.Controls;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static void RegisterEventListAutoSelectionHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(ListBox),
            ListBox.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnEventListAutoSelectionChanged));
    }

    private static void OnEventListAutoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list ||
            list.Name != "EventList" ||
            list.SelectedItem is not null ||
            list.Items.Count == 0)
        {
            return;
        }

        if (Window.GetWindow(list) is not MainWindow window || !ReferenceEquals(window.EventList, list))
            return;

        list.SelectedIndex = 0;
    }
}
