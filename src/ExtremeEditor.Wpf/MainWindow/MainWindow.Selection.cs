using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private readonly EditorSelectionState _selection = new(0);
    private bool _suppressNativeSelectionUpload;

    private void InitializeSharedSelectionState()
    {
        _selection.SetFloorCount(_level?.FloorCount ?? 0);
        _selection.Changed += SharedSelectionChanged;

        NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
    }

    private void SharedSelectionChanged(object? sender, EventArgs e)
    {
        // A tab activation uploads its target selection before committing
        // the managed tab. Do not repeat that native call after the commit.
        if (!_suppressNativeSelectionUpload)
            NativeViewport.SetSelection(_selection.SelectedFloors, _selection.PrimaryFloor);
        EnsureEditorSession();
        RefreshInspector();
        CommandManager.InvalidateRequerySuggested();
    }
}
