using System.Windows;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            NativeLevelViewport.FloorSelectionRequestedEvent,
            new EventHandler<NativeFloorSelectionRequestedEventArgs>(OnNativeFloorSelectionRequested));
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            NativeLevelViewport.EditorActionRequestedEvent,
            new EventHandler<NativeEditorActionRequestedEventArgs>(OnNativeEditorActionRequested));

        RegisterEventPropertyAutoApplyHandlers();
    }

    private static void OnNativeFloorSelectionRequested(object? sender, NativeFloorSelectionRequestedEventArgs e)
    {
        if (sender is not MainWindow window)
            return;

        window.Viewport.SetSelectedFloorFromExternal(e.Floor, e.Modifiers);
        window.NativeViewport.SetSelection(window.Viewport.SelectedFloors, window.Viewport.SelectedFloor);
        e.Handled = true;
    }

    private static void OnNativeEditorActionRequested(object? sender, NativeEditorActionRequestedEventArgs e)
    {
        if (sender is not MainWindow window)
            return;

        window.ExecuteNativeEditorAction(e.Request);
        e.Handled = true;
    }

    private void ExecuteNativeEditorAction(NativeEditorActionRequest request)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _level is null || (uint)request.Floor >= (uint)_level.FloorCount)
            return;

        if (!Viewport.SelectedFloors.Contains(request.Floor))
        {
            Viewport.SetSelectedFloorFromExternal(request.Floor);
            NativeViewport.SetSelection(Viewport.SelectedFloors, Viewport.SelectedFloor);
        }

        int[] selection = Viewport.SelectedFloors.Count > 0
            ? Viewport.SelectedFloors.ToArray()
            : [request.Floor];
        int primary = request.Floor;

        switch (request.Action)
        {
            case NativeEditorAction.InsertAngle:
            {
                int inserted = primary + 1;
                editor.InsertAngle(primary, request.Value);
                RefreshEditorAfterMutation(inserted);
                break;
            }

            case NativeEditorAction.Delete:
            {
                int target = Math.Max(0, selection.Where(floor => floor > 0).DefaultIfEmpty(1).Min() - 1);
                editor.DeleteFloors(selection);
                RefreshEditorAfterMutation(target);
                break;
            }

            case NativeEditorAction.Rotate180:
                editor.Rotate(selection, 180.0);
                RefreshEditorAfterMutation(primary, selection);
                break;

            case NativeEditorAction.InsertMidspin:
            {
                int inserted = primary + 1;
                editor.InsertMidspin(primary);
                RefreshEditorAfterMutation(inserted);
                break;
            }

            case NativeEditorAction.InsertFullTurn:
            {
                int inserted = primary + 1;
                editor.InsertFullTurn(primary);
                RefreshEditorAfterMutation(inserted);
                break;
            }

            default:
                return;
        }

        NativeViewport.SetSelection(Viewport.SelectedFloors, Viewport.SelectedFloor);
    }
}
