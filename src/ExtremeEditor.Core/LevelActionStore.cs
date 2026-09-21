using System.Collections;

namespace ExtremeEditor.Core;

public sealed class LevelActionStore
{
    private readonly LevelAction[] _actions;
    private readonly int[] _floors;
    private readonly int[] _offsets;
    private readonly IReadOnlyDictionary<int, LevelAction[]> _dictionaryView;

    private LevelActionStore(LevelAction[] actions, int[] floors, int[] offsets)
    {
        _actions = actions;
        _floors = floors;
        _offsets = offsets;
        _dictionaryView = new ActionDictionaryView(this);
    }

    public static LevelActionStore Empty { get; } = new([], [], []);

    public int ActionCount => _actions.Length;
    public int ActionFloorCount => _floors.Length;
    public IReadOnlyList<int> Floors => _floors;
    public IReadOnlyList<LevelAction> Actions => _actions;
    public IReadOnlyDictionary<int, LevelAction[]> DictionaryView => _dictionaryView;

    public static LevelActionStore Create(IEnumerable<LevelAction> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        LevelAction[] actions = source as LevelAction[] ?? source.ToArray();
        if (actions.Length == 0)
            return Empty;

        bool sorted = true;
        for (int i = 1; i < actions.Length; i++)
        {
            if (actions[i].Floor < actions[i - 1].Floor)
            {
                sorted = false;
                break;
            }
        }

        if (!sorted)
            actions = actions.OrderBy(static action => action.Floor).ToArray();

        int floorCount = 1;
        for (int i = 1; i < actions.Length; i++)
        {
            if (actions[i].Floor != actions[i - 1].Floor)
                floorCount++;
        }

        var floors = new int[floorCount];
        var offsets = new int[floorCount];
        int range = 0;
        floors[0] = actions[0].Floor;
        offsets[0] = 0;
        for (int i = 1; i < actions.Length; i++)
        {
            if (actions[i].Floor == actions[i - 1].Floor)
                continue;

            range++;
            floors[range] = actions[i].Floor;
            offsets[range] = i;
        }

        return new LevelActionStore(actions, floors, offsets);
    }

    public static LevelActionStore FromDictionary(IReadOnlyDictionary<int, LevelAction[]> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0)
            return Empty;

        var actions = new List<LevelAction>();
        foreach (KeyValuePair<int, LevelAction[]> pair in source)
            actions.AddRange(pair.Value);
        return Create(actions);
    }

    public int GetFloor(int actionFloorIndex) => _floors[actionFloorIndex];

    public ReadOnlySpan<LevelAction> GetActionsAt(int actionFloorIndex)
    {
        int offset = _offsets[actionFloorIndex];
        int end = actionFloorIndex + 1 < _offsets.Length
            ? _offsets[actionFloorIndex + 1]
            : _actions.Length;
        return _actions.AsSpan(offset, end - offset);
    }

    public bool TryGetActions(int floor, out ReadOnlySpan<LevelAction> actions)
    {
        int index = Array.BinarySearch(_floors, floor);
        if (index < 0)
        {
            actions = default;
            return false;
        }

        actions = GetActionsAt(index);
        return true;
    }

    private sealed class ActionDictionaryView : IReadOnlyDictionary<int, LevelAction[]>
    {
        private readonly LevelActionStore _store;

        public ActionDictionaryView(LevelActionStore store) => _store = store;

        public int Count => _store.ActionFloorCount;
        public IEnumerable<int> Keys => _store._floors;
        public IEnumerable<LevelAction[]> Values
        {
            get
            {
                for (int i = 0; i < _store.ActionFloorCount; i++)
                    yield return _store.GetActionsAt(i).ToArray();
            }
        }

        public LevelAction[] this[int key] =>
            TryGetValue(key, out LevelAction[] value)
                ? value
                : throw new KeyNotFoundException();

        public bool ContainsKey(int key) => Array.BinarySearch(_store._floors, key) >= 0;

        public bool TryGetValue(int key, out LevelAction[] value)
        {
            int index = Array.BinarySearch(_store._floors, key);
            if (index < 0)
            {
                value = null!;
                return false;
            }

            value = _store.GetActionsAt(index).ToArray();
            return true;
        }

        public IEnumerator<KeyValuePair<int, LevelAction[]>> GetEnumerator()
        {
            for (int i = 0; i < _store.ActionFloorCount; i++)
            {
                yield return new KeyValuePair<int, LevelAction[]>(
                    _store._floors[i],
                    _store.GetActionsAt(i).ToArray());
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
