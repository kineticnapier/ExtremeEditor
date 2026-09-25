using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public sealed class EditorSelectionState
{
    private readonly SortedSet<int> _selectedFloors = [];
    private int _floorCount;
    private int _primaryFloor = -1;
    private int _anchorFloor = -1;

    public EditorSelectionState(int floorCount)
    {
        if (floorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(floorCount));

        _floorCount = floorCount;
    }

    public IReadOnlyCollection<int> SelectedFloors => _selectedFloors;
    public int PrimaryFloor => _primaryFloor;
    public int AnchorFloor => _anchorFloor;

    public event EventHandler? Changed;

    public void SetFloorCount(int floorCount)
    {
        if (floorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(floorCount));

        int[] before = _selectedFloors.ToArray();
        int beforePrimary = _primaryFloor;
        int beforeAnchor = _anchorFloor;

        _floorCount = floorCount;
        _selectedFloors.RemoveWhere(floor => (uint)floor >= (uint)_floorCount);

        if (_selectedFloors.Count == 0)
        {
            _primaryFloor = -1;
            _anchorFloor = -1;
        }
        else
        {
            if ((uint)_primaryFloor >= (uint)_floorCount || !_selectedFloors.Contains(_primaryFloor))
                _primaryFloor = _selectedFloors.Max;
            if ((uint)_anchorFloor >= (uint)_floorCount || !_selectedFloors.Contains(_anchorFloor))
                _anchorFloor = _primaryFloor;
        }

        RaiseChangedIfNeeded(before, beforePrimary, beforeAnchor);
    }

    public void SetSelection(IEnumerable<int> floors, int primaryFloor = -1)
    {
        ArgumentNullException.ThrowIfNull(floors);

        int[] before = _selectedFloors.ToArray();
        int beforePrimary = _primaryFloor;
        int beforeAnchor = _anchorFloor;

        _selectedFloors.Clear();
        foreach (int floor in floors)
        {
            if ((uint)floor < (uint)_floorCount)
                _selectedFloors.Add(floor);
        }

        if (_selectedFloors.Count == 0)
        {
            _primaryFloor = -1;
            _anchorFloor = -1;
        }
        else
        {
            _primaryFloor = _selectedFloors.Contains(primaryFloor)
                ? primaryFloor
                : _selectedFloors.Max;
            _anchorFloor = _primaryFloor;
        }

        RaiseChangedIfNeeded(before, beforePrimary, beforeAnchor);
    }

    public void SelectFloor(int floor, ModifierKeys modifiers = ModifierKeys.None)
    {
        if ((uint)floor >= (uint)_floorCount)
        {
            SetSelection([], -1);
            return;
        }

        int[] before = _selectedFloors.ToArray();
        int beforePrimary = _primaryFloor;
        int beforeAnchor = _anchorFloor;

        bool extend = (modifiers & ModifierKeys.Shift) != 0;
        bool toggle = (modifiers & ModifierKeys.Control) != 0;

        if (extend && _anchorFloor >= 0)
        {
            int first = Math.Min(_anchorFloor, floor);
            int last = Math.Max(_anchorFloor, floor);
            if (!toggle)
                _selectedFloors.Clear();
            for (int i = first; i <= last; i++)
                _selectedFloors.Add(i);
            _primaryFloor = floor;
        }
        else if (toggle)
        {
            if (!_selectedFloors.Add(floor))
                _selectedFloors.Remove(floor);

            _primaryFloor = _selectedFloors.Contains(floor)
                ? floor
                : _selectedFloors.Count > 0 ? _selectedFloors.Max : -1;
            _anchorFloor = _primaryFloor;
        }
        else
        {
            _selectedFloors.Clear();
            _selectedFloors.Add(floor);
            _primaryFloor = floor;
            _anchorFloor = floor;
        }

        if (_selectedFloors.Count == 0)
        {
            _primaryFloor = -1;
            _anchorFloor = -1;
        }

        RaiseChangedIfNeeded(before, beforePrimary, beforeAnchor);
    }

    public void MoveSelection(int floor, bool extend)
    {
        SelectFloor(floor, extend ? ModifierKeys.Shift : ModifierKeys.None);
    }

    private void RaiseChangedIfNeeded(int[] before, int beforePrimary, int beforeAnchor)
    {
        if (beforePrimary == _primaryFloor &&
            beforeAnchor == _anchorFloor &&
            before.AsSpan().SequenceEqual(_selectedFloors.ToArray()))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
