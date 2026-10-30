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

        RegisterTrackColorEventSchemas();
        RegisterEventPropertyAutoApplyHandlers();
        RegisterReadableEventPaletteHandlers();
        RegisterEventListAutoSelectionHandlers();
    }

    private static void OnNativeFloorSelectionRequested(object? sender, NativeFloorSelectionRequestedEventArgs e)
    {
        if (sender is not MainWindow window)
            return;

        window._selection.SelectFloor(e.Floor, e.Modifiers);
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

        if (!_selection.SelectedFloors.Contains(request.Floor))
            _selection.SelectFloor(request.Floor);

        int[] selection = _selection.SelectedFloors.Count > 0
            ? _selection.SelectedFloors.ToArray()
            : [request.Floor];
        int primary = request.Floor;

        switch (request.Action)
        {
            case NativeEditorAction.PositionTrackDrag:
            {
                if (!_selection.TryGetContiguousDragRange(primary, out int firstFloor, out int lastFloor) ||
                    request.Floor != firstFloor || request.LastFloor != lastFloor)
                {
                    break;
                }

                if (editor.AddPositionTrackRangeFromDrag(firstFloor, lastFloor, request.Value, request.ValueY))
                    RefreshEditorAfterMutation(primary, selection);
                break;
            }
            case NativeEditorAction.InsertAngle:
            {
                // Use the same reverse-direction deletion logic as physical
                // ADOFAI keybinds, including Midspin parity and selection.
                ApplyAdoFaiDirection(editor, primary, request.Value);
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
    }
}
