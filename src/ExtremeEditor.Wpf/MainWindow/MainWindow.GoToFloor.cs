using System.Windows;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private void ShowGoToFloor()
    {
        if (_level is null || _level.FloorCount == 0)
            return;

        string initialValue = GoToFloorNavigation.GetInitialValue(_selection);
        string? input = GoToFloorDialog.Ask(this, initialValue);
        if (!GoToFloorNavigation.TryResolveFloor(input, _level.FloorCount, out int floor))
            return;

        GoToFloorNavigation.Apply(_selection, floor, NativeViewport.CenterFloor);
        StatusText.Text = $"Floor {floor + 1:N0}";
    }

    private void GoToFloorClick(object sender, RoutedEventArgs e) => ShowGoToFloor();
}
