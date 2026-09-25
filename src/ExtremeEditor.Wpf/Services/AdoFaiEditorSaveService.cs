using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal static class AdoFaiEditorSaveService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static async Task SaveAsync(
        EditorSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonObject root = session.GetSourceRootForSave();
        ReplaceAngles(root, session.Document.Angles);
        ReplaceActions(root, session);
        TransformDecorations(root, session.StructureEdits);
        AppendPendingDecorations(root, session.PendingDecorations);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = fullPath + ".extremeeditor.tmp";
        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, root, WriteOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, fullPath, overwrite: true);
    }

    internal static JsonObject BuildEditableActionJson(EditorSession session, LevelAction action)
    {
        JsonObject obj;
        int revision;
        if (action.PropertyOverrides is not null)
        {
            obj = (JsonObject)action.PropertyOverrides.DeepClone();
            revision = Math.Clamp(action.PropertyOverridesStructureRevision, 0, session.StructureEdits.Count);
        }
        else
        {
            obj = GetBaseActionObject(session, action);
            revision = 0;
        }

        for (int i = revision; i < session.StructureEdits.Count; i++)
            TransformObject(obj, session.StructureEdits[i], removeWhenDeleted: false);

        UpdateKnownActionProperties(obj, action);
        return obj;
    }

    private static JsonObject GetBaseActionObject(EditorSession session, LevelAction action)
    {
        JsonObject root = session.GetSourceRootForSave();
        if (action.SourceIndex >= 0 &&
            root["actions"] is JsonArray actions &&
            action.SourceIndex < actions.Count &&
            actions[action.SourceIndex] is JsonObject source)
        {
            return (JsonObject)source.DeepClone();
        }

        if (session.NewActionTemplates.TryGetValue(action.SourceIndex, out JsonObject? template))
            return (JsonObject)template.DeepClone();

        return new JsonObject();
    }

    private static void ReplaceAngles(JsonObject root, IReadOnlyList<double> angles)
    {
        var array = new JsonArray();
        foreach (double angle in angles)
            array.Add(angle);
        root["angleData"] = array;
        root.Remove("pathData");
    }

    private static void ReplaceActions(JsonObject root, EditorSession session)
    {
        JsonArray source = root["actions"] as JsonArray ?? new JsonArray();
        var transformedSource = new Dictionary<int, JsonObject>();
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i] is not JsonObject original)
                continue;

            JsonObject clone = (JsonObject)original.DeepClone();
            bool alive = true;
            foreach (FloorStructureEdit edit in session.StructureEdits)
            {
                if (!TransformObject(clone, edit, removeWhenDeleted: true))
                {
                    alive = false;
                    break;
                }
            }
            if (alive)
                transformedSource[i] = clone;
        }

        var output = new JsonArray();
        foreach (LevelAction action in session.Document.ActionStore.Actions)
        {
            JsonObject obj;
            if (action.PropertyOverrides is not null)
            {
                obj = (JsonObject)action.PropertyOverrides.DeepClone();
                int revision = Math.Clamp(action.PropertyOverridesStructureRevision, 0, session.StructureEdits.Count);
                for (int i = revision; i < session.StructureEdits.Count; i++)
                    TransformObject(obj, session.StructureEdits[i], removeWhenDeleted: false);
            }
            else if (action.SourceIndex >= 0 && transformedSource.TryGetValue(action.SourceIndex, out JsonObject? original))
            {
                obj = original;
            }
            else if (session.NewActionTemplates.TryGetValue(action.SourceIndex, out JsonObject? template))
            {
                obj = (JsonObject)template.DeepClone();
            }
            else
            {
                obj = new JsonObject();
            }

            UpdateKnownActionProperties(obj, action);
            output.Add(obj);
        }

        root["actions"] = output;
    }

    private static void TransformDecorations(JsonObject root, IReadOnlyList<FloorStructureEdit> edits)
    {
        if (root["decorations"] is not JsonArray decorations || edits.Count == 0)
            return;

        foreach (FloorStructureEdit edit in edits)
        {
            for (int i = decorations.Count - 1; i >= 0; i--)
            {
                if (decorations[i] is not JsonObject obj)
                    continue;
                if (!TransformObject(obj, edit, removeWhenDeleted: true))
                    decorations.RemoveAt(i);
            }
        }
    }

    private static void AppendPendingDecorations(JsonObject root, IReadOnlyList<PendingDecoration> pending)
    {
        if (pending.Count == 0)
            return;

        JsonArray decorations = root["decorations"] as JsonArray ?? new JsonArray();
        if (root["decorations"] is null)
            root["decorations"] = decorations;

        foreach (PendingDecoration item in pending)
        {
            JsonObject node = (JsonObject)item.Node.DeepClone();
            node["floor"] = item.Floor;
            decorations.Add(node);
        }
    }

    internal static bool TransformObject(JsonObject obj, FloorStructureEdit edit, bool removeWhenDeleted)
    {
        if (!EditorSession.TryGetInt(obj["floor"], out int oldFloor))
            return true;

        if (edit.Kind == FloorStructureEditKind.Delete &&
            oldFloor >= edit.Floor && oldFloor < edit.Floor + edit.Count)
        {
            return !removeWhenDeleted;
        }

        TransformRelativeReference(obj, "startTile", oldFloor, edit);
        TransformRelativeReference(obj, "endTile", oldFloor, edit);
        obj["floor"] = MapFloor(oldFloor, edit);
        return true;
    }

    private static void TransformRelativeReference(
        JsonObject obj,
        string property,
        int eventFloor,
        FloorStructureEdit edit)
    {
        JsonNode? node = obj[property];
        if (node is JsonArray pair && pair.Count >= 2 &&
            EditorSession.TryGetInt(pair[0], out int offset) &&
            pair[1] is JsonValue modeValue &&
            modeValue.TryGetValue(out string? mode))
        {
            int oldTarget;
            if (string.Equals(mode, "ThisTile", StringComparison.OrdinalIgnoreCase))
                oldTarget = eventFloor + offset;
            else if (string.Equals(mode, "Start", StringComparison.OrdinalIgnoreCase))
                oldTarget = offset;
            else if (string.Equals(mode, "End", StringComparison.OrdinalIgnoreCase))
                oldTarget = edit.BeforeFloorCount - 1 + offset;
            else
                return;

            int newEventFloor = MapFloor(eventFloor, edit);
            int newTarget = MapReferenceFloor(oldTarget, edit);
            int newFloorCount = edit.Kind == FloorStructureEditKind.Insert
                ? edit.BeforeFloorCount + edit.Count
                : Math.Max(1, edit.BeforeFloorCount - edit.Count);

            int newOffset = string.Equals(mode, "ThisTile", StringComparison.OrdinalIgnoreCase)
                ? newTarget - newEventFloor
                : string.Equals(mode, "End", StringComparison.OrdinalIgnoreCase)
                    ? newTarget - (newFloorCount - 1)
                    : newTarget;
            pair[0] = newOffset;
            return;
        }

        if (EditorSession.TryGetInt(node, out int absolute))
            obj[property] = MapReferenceFloor(absolute, edit);
    }

    private static int MapFloor(int floor, FloorStructureEdit edit)
    {
        if (edit.Kind == FloorStructureEditKind.Insert)
            return floor > edit.Floor ? floor + edit.Count : floor;

        int end = edit.Floor + edit.Count;
        if (floor < edit.Floor)
            return floor;
        if (floor >= end)
            return floor - edit.Count;
        return Math.Min(edit.Floor, Math.Max(0, edit.BeforeFloorCount - edit.Count - 1));
    }

    private static int MapReferenceFloor(int floor, FloorStructureEdit edit) => MapFloor(floor, edit);

    internal static void UpdateKnownActionProperties(JsonObject obj, LevelAction action)
    {
        obj["floor"] = action.Floor;
        obj["eventType"] = action.EventType;
        obj["active"] = action.Active;
        SetOrRemove(obj, "speedType", action.SpeedType);
        SetOrRemove(obj, "beatsPerMinute", action.BeatsPerMinute);
        SetOrRemove(obj, "bpmMultiplier", action.BpmMultiplier);
        SetOrRemove(obj, "icon", action.CustomIcon);
        SetOrRemove(obj, "hitsound", action.HitSound);
        SetOrRemove(obj, "hitsoundVolume", action.HitSoundVolumePercent);
        SetOrRemove(obj, "gameSound", action.GameSound);
        SetOrRemove(obj, "planets", action.Planets);
        SetOrRemove(obj, "angleOffset", action.AngleOffset);
        SetOrRemove(obj, "duration", action.Duration);
    }

    private static void SetOrRemove(JsonObject obj, string property, string? value)
    {
        if (value is null)
            obj.Remove(property);
        else
            obj[property] = value;
    }

    private static void SetOrRemove(JsonObject obj, string property, double? value)
    {
        if (value is null)
            obj.Remove(property);
        else
            obj[property] = value.Value;
    }
}
