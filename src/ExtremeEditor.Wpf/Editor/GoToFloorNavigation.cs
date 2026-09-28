using System.Globalization;

namespace ExtremeEditor.Wpf;

internal static class GoToFloorNavigation
{
    internal static bool TryResolveFloor(string? text, int floorCount, out int floor)
    {
        floor = -1;
        if (floorCount <= 0 ||
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long displayFloor))
        {
            return false;
        }

        long clamped = Math.Clamp(displayFloor, 1L, floorCount);
        floor = checked((int)(clamped - 1L));
        return true;
    }

    internal static string GetInitialValue(EditorSelectionState selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        int floor = selection.PrimaryFloor >= 0
            ? selection.PrimaryFloor
            : selection.AnchorFloor >= 0 ? selection.AnchorFloor : 0;
        return ((long)floor + 1L).ToString(CultureInfo.InvariantCulture);
    }

    internal static void Apply(EditorSelectionState selection, int floor, Action<int> centerFloor)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(centerFloor);

        selection.SetSelection([floor], floor);
        centerFloor(floor);
    }
}
