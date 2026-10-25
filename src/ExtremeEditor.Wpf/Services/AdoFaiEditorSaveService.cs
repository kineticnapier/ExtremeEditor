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

        JsonObject root = BuildSaveRoot(session, path);
        await WriteAsync(root, path, cancellationToken).ConfigureAwait(false);
    }

    internal static JsonObject BuildSaveRoot(EditorSession session, string path)
    {
        JsonObject root = session.GetSourceRootForSave();
        ReplaceSettings(root, session.Document);
        ReplaceAngles(root, session.Document.Angles);
        ReplaceActions(root, session);
        ReplaceDecorations(root, session.Document.Decorations);
        RebaseRelativeAssetReferences(root, session.Document.SourcePath, path);
        return root;
    }

    internal static async Task WriteAsync(
        JsonObject root,
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = fullPath + $".{Guid.NewGuid():N}.extremeeditor.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
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
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    internal static void RebaseRelativeAssetReferences(
        JsonObject root,
        string sourcePath,
        string targetPath,
        bool requireExisting = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath == "<synthetic>")
            return;

        string sourceFullPath = Path.GetFullPath(sourcePath);
        string targetFullPath = Path.GetFullPath(targetPath);
        string? sourceDirectory = Path.GetDirectoryName(sourceFullPath);
        string? targetDirectory = Path.GetDirectoryName(targetFullPath);
        if (sourceDirectory is null || targetDirectory is null ||
            string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RebaseProperty(root["settings"] as JsonObject, "songFilename", sourcePath, targetPath, requireExisting);
        RebaseProperty(root["settings"] as JsonObject, "bgImage", sourcePath, targetPath, requireExisting);

        if (root["actions"] is JsonArray actions)
        {
            foreach (JsonObject action in actions.OfType<JsonObject>())
            {
                RebaseProperty(action, "bgImage", sourcePath, targetPath, requireExisting);
                RebaseProperty(action, "decorationImage", sourcePath, targetPath, requireExisting);
            }
        }

        if (root["decorations"] is JsonArray decorations)
        {
            foreach (JsonObject decoration in decorations.OfType<JsonObject>())
                RebaseProperty(decoration, "decorationImage", sourcePath, targetPath, requireExisting);
        }
    }

    private static void RebaseProperty(
        JsonObject? owner,
        string property,
        string sourcePath,
        string targetPath,
        bool requireExisting)
    {
        if (owner?[property] is not JsonValue value ||
            !value.TryGetValue(out string? reference) ||
            string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        owner[property] = RebaseRelativeAssetReference(
            reference,
            sourcePath,
            targetPath,
            requireExisting);
    }

    internal static string? RebaseRelativeAssetReference(
        string? reference,
        string sourcePath,
        string targetPath,
        bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) ||
            string.IsNullOrWhiteSpace(sourcePath) || sourcePath == "<synthetic>")
        {
            return reference;
        }

        string? sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        string? targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (sourceDirectory is null || targetDirectory is null ||
            string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        string sourceAsset = Path.GetFullPath(Path.Combine(sourceDirectory, reference));
        if (requireExisting && !File.Exists(sourceAsset))
        {
            throw new FileNotFoundException(
                "Cannot Save As because an asset reference does not resolve from the source chart.",
                sourceAsset);
        }

        return Path.GetRelativePath(targetDirectory, sourceAsset);
    }

    private static void ReplaceSettings(JsonObject root, LevelDocument document)
    {
        JsonObject settings = root["settings"] is JsonObject source
            ? (JsonObject)source.DeepClone()
            : new JsonObject();
        settings["songFilename"] = document.SongFilename;
        settings["bpm"] = document.InitialBpm;
        settings["volume"] = document.SongVolumePercent;
        settings["offset"] = document.OffsetMilliseconds;
        settings["pitch"] = document.PitchPercent;
        settings["hitsound"] = document.DefaultHitSound;
        settings["hitsoundVolume"] = document.HitSoundVolumePercent;
        root["settings"] = settings;
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
        if (session.ActionSourceTemplates.TryGetValue(action.SourceIndex, out JsonObject? stableTemplate))
            return (JsonObject)stableTemplate.DeepClone();

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
            else if (session.ActionSourceTemplates.TryGetValue(action.SourceIndex, out JsonObject? stableTemplate))
            {
                obj = (JsonObject)stableTemplate.DeepClone();
                foreach (FloorStructureEdit edit in session.StructureEdits)
                    TransformObject(obj, edit, removeWhenDeleted: false);
            }
            else if (action.SourceIndex >= 0 && transformedSource.TryGetValue(action.SourceIndex, out JsonObject? original))
            {
                obj = (JsonObject)original.DeepClone();
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

    private static void ReplaceDecorations(
        JsonObject root,
        IReadOnlyList<LevelDecoration> decorations)
    {
        var output = new JsonArray();
        foreach (LevelDecoration decoration in decorations)
            output.Add(BuildDecorationJson(decoration));
        root["decorations"] = output;
    }

    internal static JsonObject BuildDecorationJson(LevelDecoration decoration)
    {
        var result = (JsonObject)decoration.Properties.DeepClone();
        result.Remove("floor");
        result.Remove("eventType");
        var output = new JsonObject();
        if (decoration.Floor is int floor)
            output["floor"] = floor;
        output["eventType"] = decoration.EventType;
        foreach ((string name, JsonNode? value) in result.ToArray())
        {
            result.Remove(name);
            output[name] = value;
        }
        return output;
    }

    internal static bool TryTransformDecoration(
        LevelDecoration decoration,
        FloorStructureEdit edit,
        bool removeWhenDeleted,
        out LevelDecoration transformed)
    {
        if (decoration.Floor is null)
        {
            transformed = decoration;
            return true;
        }

        JsonObject node = BuildDecorationJson(decoration);
        if (!TransformObject(node, edit, removeWhenDeleted))
        {
            transformed = decoration;
            return false;
        }

        if (!EditorSession.TryGetInt(node["floor"], out int floor))
            floor = decoration.Floor.Value;
        string eventType = node["eventType"]?.GetValue<string>() ?? decoration.EventType;
        node.Remove("floor");
        node.Remove("eventType");
        transformed = decoration with
        {
            Floor = floor,
            EventType = eventType,
            Properties = node
        };
        return true;
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
