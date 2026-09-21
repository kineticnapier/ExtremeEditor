using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal enum FloorStructureEditKind
{
    Insert,
    Delete
}

internal readonly record struct FloorStructureEdit(
    FloorStructureEditKind Kind,
    int Floor,
    int Count,
    int BeforeFloorCount);

internal sealed class EditorSession
{
    private readonly List<double> _angles;
    private readonly List<LevelAction> _actions;
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();
    private readonly List<FloorStructureEdit> _structureEdits = new();
    private readonly Dictionary<int, JsonObject> _newActionTemplates = new();
    private readonly List<PendingDecoration> _pendingDecorations = new();

    private EditorClipboard? _clipboard;
    private JsonObject? _sourceRoot;
    private bool _sourceMappingInitialized;
    private int _nextNewSourceIndex = -1;
    private int _historyPosition;
    private int _savedHistoryPosition;

    public EditorSession(LevelDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _angles = document.Angles.ToList();
        _actions = document.ActionStore.Actions.ToList();
    }

    public LevelDocument Document { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasClipboard => _clipboard is { Angles.Length: > 0 };
    public bool IsDirty => _historyPosition != _savedHistoryPosition;
    public string? UndoName => _undo.TryPeek(out IEditorCommand? command) ? command.Name : null;
    public string? RedoName => _redo.TryPeek(out IEditorCommand? command) ? command.Name : null;
    public IReadOnlyList<FloorStructureEdit> StructureEdits => _structureEdits;
    public IReadOnlyDictionary<int, JsonObject> NewActionTemplates => _newActionTemplates;
    public IReadOnlyList<PendingDecoration> PendingDecorations => _pendingDecorations;

    public event EventHandler? Changed;

    public IReadOnlyList<LevelAction> GetActionsAtFloor(int floor)
    {
        if (!Document.ActionStore.TryGetActions(floor, out ReadOnlySpan<LevelAction> actions))
            return Array.Empty<LevelAction>();
        return actions.ToArray();
    }

    public void InsertAngle(int afterFloor, double angle)
    {
        Execute(new InsertFloorsCommand(afterFloor, [NormalizeAngle(angle)], [], [], "Insert floor"));
    }

    public void InsertMidspin(int afterFloor)
    {
        Execute(new InsertFloorsCommand(afterFloor, [999.0], [], [], "Insert Midspin"));
    }

    public void InsertFullTurn(int afterFloor)
    {
        int floor = Math.Clamp(afterFloor, 0, Document.FloorCount - 1);
        double entryDegrees = GetEntryAngleDegrees(floor);
        double rawAngle = NormalizeAngle(90.0 - entryDegrees);
        Execute(new InsertFloorsCommand(floor, [rawAngle], [], [], "Insert 360° floor"));
    }

    public void DeleteFloors(IEnumerable<int> selectedFloors)
    {
        int[] floors = selectedFloors
            .Where(floor => floor > 0 && floor < Document.FloorCount)
            .Distinct()
            .OrderBy(floor => floor)
            .ToArray();
        if (floors.Length == 0)
            return;

        EnsureSourceMapping();
        var ranges = new List<(int First, int Last)>();
        int first = floors[0];
        int last = first;
        for (int i = 1; i < floors.Length; i++)
        {
            if (floors[i] == last + 1)
            {
                last = floors[i];
                continue;
            }
            ranges.Add((first, last));
            first = last = floors[i];
        }
        ranges.Add((first, last));

        IEditorCommand[] commands = ranges
            .OrderByDescending(range => range.First)
            .Select(range => (IEditorCommand)new DeleteFloorRangeCommand(range.First, range.Last))
            .ToArray();
        Execute(commands.Length == 1 ? commands[0] : new CompositeEditorCommand("Delete floors", commands));
    }

    public void Rotate(IEnumerable<int> selectedFloors, double degrees)
    {
        TransformSelectedAngles(selectedFloors, $"Rotate {degrees:+0;-0;0}°", angle => NormalizeAngle(angle + degrees));
    }

    public void FlipHorizontal(IEnumerable<int> selectedFloors)
    {
        TransformSelectedAngles(selectedFloors, "Flip horizontal", angle => NormalizeAngle(180.0 - angle));
    }

    public void FlipVertical(IEnumerable<int> selectedFloors)
    {
        TransformSelectedAngles(selectedFloors, "Flip vertical", angle => NormalizeAngle(-angle));
    }

    public void CopyFloors(IEnumerable<int> selectedFloors)
    {
        int[] floors = selectedFloors
            .Where(floor => floor > 0 && floor < Document.FloorCount)
            .Distinct()
            .OrderBy(floor => floor)
            .ToArray();
        if (floors.Length == 0)
            return;

        EnsureSourceMapping();
        JsonObject root = EnsureSourceRoot();
        int firstFloor = floors[0];
        var floorSet = floors.ToHashSet();
        double[] angles = floors.Select(floor => _angles[floor - 1]).ToArray();

        var actions = new List<ClipboardAction>();
        foreach (LevelAction action in _actions)
        {
            if (!floorSet.Contains(action.Floor))
                continue;

            JsonObject? template = GetSourceActionTemplate(root, action.SourceIndex);
            actions.Add(new ClipboardAction(
                action.Floor - firstFloor,
                action with { Floor = action.Floor - firstFloor, SourceIndex = -1 },
                template));
        }

        var decorations = new List<ClipboardDecoration>();
        if (root["decorations"] is JsonArray decorationArray)
        {
            foreach (JsonNode? node in decorationArray)
            {
                if (node is not JsonObject obj || !TryGetInt(obj["floor"], out int floor) || !floorSet.Contains(floor))
                    continue;
                decorations.Add(new ClipboardDecoration(floor - firstFloor, (JsonObject)obj.DeepClone()));
            }
        }

        _clipboard = new EditorClipboard(angles, actions.ToArray(), decorations.ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CutFloors(IEnumerable<int> selectedFloors)
    {
        int[] floors = selectedFloors.ToArray();
        CopyFloors(floors);
        DeleteFloors(floors);
    }

    public void PasteFloors(int afterFloor)
    {
        if (_clipboard is not { Angles.Length: > 0 } clipboard)
            return;

        EnsureSourceMapping();
        Execute(new InsertFloorsCommand(
            afterFloor,
            clipboard.Angles,
            clipboard.Actions,
            clipboard.Decorations,
            "Paste floors"));
    }

    public void AddAction(int floor, string eventType)
    {
        if ((uint)floor >= (uint)Document.FloorCount || string.IsNullOrWhiteSpace(eventType))
            return;
        EnsureSourceMapping();
        var action = new LevelAction(floor, eventType.Trim(), true, null, null, null, null)
        {
            SourceIndex = NextNewSourceIndex()
        };
        Execute(new AddActionCommand(action));
    }

    public void DeleteAction(LevelAction action)
    {
        EnsureSourceMapping();
        Execute(new DeleteActionCommand(action));
    }

    public void ReplaceAction(LevelAction oldAction, LevelAction updatedAction)
    {
        EnsureSourceMapping();
        updatedAction = updatedAction with
        {
            Floor = Math.Clamp(updatedAction.Floor, 0, Math.Max(0, Document.FloorCount - 1)),
            SourceIndex = oldAction.SourceIndex,
            Kind = LevelActionKinds.FromEventType(updatedAction.EventType)
        };
        Execute(new ReplaceActionCommand(oldAction, updatedAction));
    }

    public void Undo()
    {
        if (!_undo.TryPop(out IEditorCommand? command))
            return;
        command.Undo(this);
        _redo.Push(command);
        _historyPosition--;
        OnChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out IEditorCommand? command))
            return;
        command.Execute(this);
        _undo.Push(command);
        _historyPosition++;
        OnChanged();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureSourceMapping();
        await AdoFaiEditorSaveService.SaveAsync(this, path, cancellationToken).ConfigureAwait(false);
        Document.SourcePath = path;
        _savedHistoryPosition = _historyPosition;
        RebaseSource(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal JsonObject GetSourceRootForSave() => (JsonObject)EnsureSourceRoot().DeepClone();

    internal void InsertRaw(
        int afterFloor,
        IReadOnlyList<double> angles,
        IReadOnlyList<ClipboardAction> clipboardActions,
        IReadOnlyList<ClipboardDecoration> clipboardDecorations,
        List<LevelAction>? createdActions,
        List<PendingDecoration>? createdDecorations,
        bool recordStructure)
    {
        int beforeFloorCount = Document.FloorCount;
        int clampedAfter = Math.Clamp(afterFloor, 0, Math.Max(0, beforeFloorCount - 1));
        int insertIndex = Math.Clamp(clampedAfter, 0, _angles.Count);
        int count = angles.Count;
        if (count == 0)
            return;

        _angles.InsertRange(insertIndex, angles);
        for (int i = 0; i < _actions.Count; i++)
        {
            LevelAction action = _actions[i];
            if (action.Floor > clampedAfter)
                _actions[i] = action with { Floor = action.Floor + count };
        }

        if (createdActions is null || createdActions.Count == 0)
        {
            createdActions?.Clear();
            foreach (ClipboardAction item in clipboardActions)
            {
                int sourceIndex = NextNewSourceIndex();
                LevelAction created = item.Action with
                {
                    Floor = clampedAfter + 1 + item.RelativeFloor,
                    SourceIndex = sourceIndex,
                    Kind = LevelActionKinds.FromEventType(item.Action.EventType)
                };
                _actions.Add(created);
                createdActions?.Add(created);
                if (item.Template is not null)
                    _newActionTemplates[sourceIndex] = (JsonObject)item.Template.DeepClone();
            }
        }
        else
        {
            foreach (LevelAction created in createdActions)
                _actions.Add(created);
        }

        if (createdDecorations is null || createdDecorations.Count == 0)
        {
            createdDecorations?.Clear();
            foreach (ClipboardDecoration item in clipboardDecorations)
            {
                var node = (JsonObject)item.Node.DeepClone();
                int floor = clampedAfter + 1 + item.RelativeFloor;
                node["floor"] = floor;
                var pending = new PendingDecoration(Guid.NewGuid(), floor, node);
                _pendingDecorations.Add(pending);
                createdDecorations?.Add(pending);
            }
        }
        else
        {
            _pendingDecorations.AddRange(createdDecorations);
        }

        if (recordStructure)
            _structureEdits.Add(new FloorStructureEdit(FloorStructureEditKind.Insert, clampedAfter, count, beforeFloorCount));
        RebuildDocument();
    }

    internal DeletedRange DeleteRaw(int firstFloor, int lastFloor, bool recordStructure)
    {
        int beforeFloorCount = Document.FloorCount;
        int first = Math.Clamp(firstFloor, 1, Math.Max(1, beforeFloorCount - 1));
        int last = Math.Clamp(lastFloor, first, beforeFloorCount - 1);
        int count = last - first + 1;
        double[] removedAngles = _angles.GetRange(first - 1, count).ToArray();
        _angles.RemoveRange(first - 1, count);

        LevelAction[] removedActions = _actions
            .Where(action => action.Floor >= first && action.Floor <= last)
            .ToArray();
        _actions.RemoveAll(action => action.Floor >= first && action.Floor <= last);
        for (int i = 0; i < _actions.Count; i++)
        {
            LevelAction action = _actions[i];
            if (action.Floor > last)
                _actions[i] = action with { Floor = action.Floor - count };
        }

        PendingDecoration[] removedDecorations = _pendingDecorations
            .Where(item => item.Floor >= first && item.Floor <= last)
            .ToArray();
        _pendingDecorations.RemoveAll(item => item.Floor >= first && item.Floor <= last);
        for (int i = 0; i < _pendingDecorations.Count; i++)
        {
            PendingDecoration item = _pendingDecorations[i];
            if (item.Floor > last)
            {
                int floor = item.Floor - count;
                item.Node["floor"] = floor;
                _pendingDecorations[i] = item with { Floor = floor };
            }
        }

        if (recordStructure)
            _structureEdits.Add(new FloorStructureEdit(FloorStructureEditKind.Delete, first, count, beforeFloorCount));
        RebuildDocument();
        return new DeletedRange(first, removedAngles, removedActions, removedDecorations);
    }

    internal void RestoreDeletedRaw(DeletedRange deleted, bool removeLastStructureEdit)
    {
        int afterFloor = deleted.FirstFloor - 1;
        int count = deleted.Angles.Length;
        _angles.InsertRange(afterFloor, deleted.Angles);
        for (int i = 0; i < _actions.Count; i++)
        {
            LevelAction action = _actions[i];
            if (action.Floor >= deleted.FirstFloor)
                _actions[i] = action with { Floor = action.Floor + count };
        }
        _actions.AddRange(deleted.Actions);

        for (int i = 0; i < _pendingDecorations.Count; i++)
        {
            PendingDecoration item = _pendingDecorations[i];
            if (item.Floor >= deleted.FirstFloor)
            {
                int floor = item.Floor + count;
                item.Node["floor"] = floor;
                _pendingDecorations[i] = item with { Floor = floor };
            }
        }
        _pendingDecorations.AddRange(deleted.Decorations);

        if (removeLastStructureEdit && _structureEdits.Count > 0)
            _structureEdits.RemoveAt(_structureEdits.Count - 1);
        RebuildDocument();
    }

    internal void RemoveInsertedRaw(int firstFloor, int count, bool removeLastStructureEdit)
    {
        DeleteRaw(firstFloor, firstFloor + count - 1, recordStructure: false);
        if (removeLastStructureEdit && _structureEdits.Count > 0)
            _structureEdits.RemoveAt(_structureEdits.Count - 1);
    }

    internal void SetAngleValues(int[] angleIndices, double[] values)
    {
        for (int i = 0; i < angleIndices.Length; i++)
            _angles[angleIndices[i]] = values[i];
        RebuildDocument();
    }

    internal void AddActionRaw(LevelAction action)
    {
        _actions.Add(action);
        RebuildDocument();
    }

    internal void RemoveActionRaw(LevelAction action)
    {
        int index = FindActionIndex(action);
        if (index >= 0)
            _actions.RemoveAt(index);
        RebuildDocument();
    }

    internal void ReplaceActionRaw(LevelAction oldAction, LevelAction newAction)
    {
        int index = FindActionIndex(oldAction);
        if (index >= 0)
            _actions[index] = newAction;
        RebuildDocument();
    }

    private void Execute(IEditorCommand command)
    {
        command.Execute(this);
        _undo.Push(command);
        _redo.Clear();
        _historyPosition++;
        OnChanged();
    }

    private void TransformSelectedAngles(IEnumerable<int> selectedFloors, string name, Func<double, double> transform)
    {
        int[] indices = selectedFloors
            .Where(floor => floor > 0 && floor < Document.FloorCount)
            .Select(floor => floor - 1)
            .Distinct()
            .OrderBy(index => index)
            .Where(index => Math.Abs(_angles[index] - 999.0) > 0.000001)
            .ToArray();
        if (indices.Length == 0)
            return;

        double[] before = indices.Select(index => _angles[index]).ToArray();
        double[] after = before.Select(transform).ToArray();
        Execute(new SetAnglesCommand(name, indices, before, after));
    }

    private void RebuildDocument()
    {
        _actions.Sort(static (a, b) => a.Floor.CompareTo(b.Floor));
        RecomputeSpeedRatios();
        Document.Angles = _angles.ToArray();
        Document.ReplaceActions(_actions);
        Document.RebuildGeometry();
    }

    private void RecomputeSpeedRatios()
    {
        double bpm = Document.InitialBpm > 0 ? Document.InitialBpm : 100.0;
        foreach (LevelAction action in _actions)
        {
            action.SpeedRatio = null;
            if (!action.Active || action.Kind != LevelActionKind.SetSpeed)
                continue;

            if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                action.BpmMultiplier is double multiplier && multiplier > 0)
            {
                action.SpeedRatio = multiplier;
                bpm *= multiplier;
            }
            else if (action.BeatsPerMinute is double target && target > 0)
            {
                action.SpeedRatio = bpm > 0 ? target / bpm : null;
                bpm = target;
            }
        }
    }

    private double GetEntryAngleDegrees(int floor)
    {
        double entry = 270.0;
        int max = Math.Min(floor, _angles.Count);
        for (int i = 0; i < max; i++)
        {
            double raw = _angles[i];
            double exit = Math.Abs(raw - 999.0) < 0.000001
                ? entry
                : NormalizeAngle(90.0 - raw);
            entry = NormalizeAngle(exit + 180.0);
        }
        return entry;
    }

    private int FindActionIndex(LevelAction action)
    {
        if (action.SourceIndex != -1)
        {
            int bySource = _actions.FindIndex(candidate => candidate.SourceIndex == action.SourceIndex);
            if (bySource >= 0)
                return bySource;
        }
        return _actions.IndexOf(action);
    }

    private int NextNewSourceIndex() => _nextNewSourceIndex--;

    private JsonObject EnsureSourceRoot()
    {
        if (_sourceRoot is not null)
            return _sourceRoot;
        if (Document.SourcePath == "<synthetic>" || !File.Exists(Document.SourcePath))
            return _sourceRoot = new JsonObject
            {
                ["angleData"] = new JsonArray(),
                ["settings"] = new JsonObject(),
                ["actions"] = new JsonArray(),
                ["decorations"] = new JsonArray()
            };

        JsonNode? node = JsonNode.Parse(File.ReadAllText(Document.SourcePath), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        return _sourceRoot = node as JsonObject ?? throw new InvalidDataException("ADOFAI root must be a JSON object.");
    }

    private void EnsureSourceMapping()
    {
        if (_sourceMappingInitialized)
            return;

        JsonObject root = EnsureSourceRoot();
        var queues = new Dictionary<(int Floor, string Type), Queue<int>>();
        if (root["actions"] is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject obj ||
                    !TryGetInt(obj["floor"], out int floor))
                    continue;
                string type = obj["eventType"]?.GetValue<string>() ?? "<unknown>";
                var key = (floor, type);
                if (!queues.TryGetValue(key, out Queue<int>? queue))
                    queues[key] = queue = new Queue<int>();
                queue.Enqueue(i);
            }
        }

        for (int i = 0; i < _actions.Count; i++)
        {
            LevelAction action = _actions[i];
            if (action.SourceIndex >= 0)
                continue;
            if (queues.TryGetValue((action.Floor, action.EventType), out Queue<int>? queue) && queue.Count > 0)
                _actions[i] = action with { SourceIndex = queue.Dequeue() };
            else
                _actions[i] = action with { SourceIndex = NextNewSourceIndex() };
        }

        _sourceMappingInitialized = true;
        RebuildDocument();
    }

    private void RebaseSource(string path)
    {
        _sourceRoot = null;
        _sourceMappingInitialized = false;
        _structureEdits.Clear();
        _newActionTemplates.Clear();
        _pendingDecorations.Clear();
        _nextNewSourceIndex = -1;
        EnsureSourceMapping();
    }

    private static JsonObject? GetSourceActionTemplate(JsonObject root, int sourceIndex)
    {
        if (sourceIndex < 0 || root["actions"] is not JsonArray actions || sourceIndex >= actions.Count)
            return null;
        return actions[sourceIndex] is JsonObject obj ? (JsonObject)obj.DeepClone() : null;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static bool TryGetInt(JsonNode? node, out int value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out value))
                return true;
            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out value))
                return true;
        }
        value = 0;
        return false;
    }

    internal static double NormalizeAngle(double value)
    {
        double result = value % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    private interface IEditorCommand
    {
        string Name { get; }
        void Execute(EditorSession session);
        void Undo(EditorSession session);
    }

    private sealed class CompositeEditorCommand(string name, IReadOnlyList<IEditorCommand> commands) : IEditorCommand
    {
        public string Name => name;
        public void Execute(EditorSession session)
        {
            foreach (IEditorCommand command in commands)
                command.Execute(session);
        }
        public void Undo(EditorSession session)
        {
            for (int i = commands.Count - 1; i >= 0; i--)
                commands[i].Undo(session);
        }
    }

    private sealed class SetAnglesCommand(
        string name,
        int[] indices,
        double[] before,
        double[] after) : IEditorCommand
    {
        public string Name => name;
        public void Execute(EditorSession session) => session.SetAngleValues(indices, after);
        public void Undo(EditorSession session) => session.SetAngleValues(indices, before);
    }

    private sealed class InsertFloorsCommand(
        int afterFloor,
        IReadOnlyList<double> angles,
        IReadOnlyList<ClipboardAction> actions,
        IReadOnlyList<ClipboardDecoration> decorations,
        string name) : IEditorCommand
    {
        private readonly List<LevelAction> _createdActions = [];
        private readonly List<PendingDecoration> _createdDecorations = [];
        private int _firstInsertedFloor;

        public string Name => name;

        public void Execute(EditorSession session)
        {
            int clampedAfter = Math.Clamp(afterFloor, 0, Math.Max(0, session.Document.FloorCount - 1));
            _firstInsertedFloor = clampedAfter + 1;
            session.InsertRaw(
                clampedAfter,
                angles,
                actions,
                decorations,
                _createdActions,
                _createdDecorations,
                recordStructure: true);
        }

        public void Undo(EditorSession session)
        {
            session.RemoveInsertedRaw(_firstInsertedFloor, angles.Count, removeLastStructureEdit: true);
            foreach (LevelAction action in _createdActions)
                session._newActionTemplates.Remove(action.SourceIndex);
        }
    }

    private sealed class DeleteFloorRangeCommand(int firstFloor, int lastFloor) : IEditorCommand
    {
        private DeletedRange? _deleted;
        public string Name => firstFloor == lastFloor ? "Delete floor" : "Delete floor range";

        public void Execute(EditorSession session)
        {
            _deleted = session.DeleteRaw(firstFloor, lastFloor, recordStructure: true);
        }

        public void Undo(EditorSession session)
        {
            if (_deleted is DeletedRange deleted)
                session.RestoreDeletedRaw(deleted, removeLastStructureEdit: true);
        }
    }

    private sealed class AddActionCommand(LevelAction action) : IEditorCommand
    {
        public string Name => "Add event";
        public void Execute(EditorSession session) => session.AddActionRaw(action);
        public void Undo(EditorSession session) => session.RemoveActionRaw(action);
    }

    private sealed class DeleteActionCommand(LevelAction action) : IEditorCommand
    {
        public string Name => "Delete event";
        public void Execute(EditorSession session) => session.RemoveActionRaw(action);
        public void Undo(EditorSession session) => session.AddActionRaw(action);
    }

    private sealed class ReplaceActionCommand(LevelAction before, LevelAction after) : IEditorCommand
    {
        public string Name => "Edit event";
        public void Execute(EditorSession session) => session.ReplaceActionRaw(before, after);
        public void Undo(EditorSession session) => session.ReplaceActionRaw(after, before);
    }
}

internal sealed record EditorClipboard(
    double[] Angles,
    ClipboardAction[] Actions,
    ClipboardDecoration[] Decorations);

internal sealed record ClipboardAction(int RelativeFloor, LevelAction Action, JsonObject? Template);
internal sealed record ClipboardDecoration(int RelativeFloor, JsonObject Node);
internal sealed record PendingDecoration(Guid Id, int Floor, JsonObject Node);
internal sealed record DeletedRange(
    int FirstFloor,
    double[] Angles,
    LevelAction[] Actions,
    PendingDecoration[] Decorations);
