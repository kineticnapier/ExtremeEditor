namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private readonly EditorSelectionState _selection = new(0);

    private void InitializeSharedSelectionState()
    {
        int[] previousSelection = Viewport.SelectedFloors.ToArray();
        int previousPrimary = Viewport.SelectedFloor;

        _selection.SetFloorCount(_level?.FloorCount ?? 0);
        _selection.SetSelection(previousSelection, previousPrimary);
        Viewport.SelectionState = _selection;
        _selection.Changed += SharedSelectionChanged;

        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
    }

    private void SharedSelectionChanged(object? sender, EventArgs e)
    {
        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
    }
}
