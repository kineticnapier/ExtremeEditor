using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class MiscSettingsSliceRegression
{
    private const string SnapshotTypeName = "ExtremeEditor.Wpf.MiscSettingsSnapshot";

    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "load/default semantics", VerifyLoadDefaults);
        Check(failures, "typed snapshot contract", VerifyTypedContract);
        Check(failures, "edit/undo three-layer coherence", VerifyEditUndoCoherence);
        Check(failures, "save ownership and preservation", VerifySavePreservation);
        Check(failures, "renderer-facing root and PositionTrack semantics", VerifyRendererFacingState);
        Check(failures, "runtime edit and undo propagation", VerifyRuntimeEditPropagation);
        Check(failures, "unsupported settings boundary", VerifyUnsupportedBoundary);
        Check(failures, "five-category sidebar contract", VerifySidebarContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Misc Settings Slice is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyLoadDefaults()
    {
        foreach ((string label, JsonNode? value, bool expected) in new[]
                 {
                     ("missing", (JsonNode?)null, true),
                     ("explicit true", JsonValue.Create(true), true),
                     ("explicit false", JsonValue.Create(false), false)
                 })
        {
            string path = WriteFixture($"Load-{label}", value, includePositionTrack: false);
            try
            {
                TrackTransformSourceData source = TrackTransformSourceReader.Load(path);
                if (source.DefaultStickToFloors != expected)
                    throw new InvalidOperationException(
                        $"{label}: expected DefaultStickToFloors={expected}, actual={source.DefaultStickToFloors}.");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static void VerifyTypedContract()
    {
        MiscContract contract = MiscContract.Discover();
        string path = WriteFixture("Typed", JsonValue.Create(false), includePositionTrack: false);
        try
        {
            (EditorSession session, _) = OpenSession(path);
            if (contract.Read(contract.Get(session)))
                throw new InvalidOperationException("typed snapshot did not read explicit false.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyEditUndoCoherence()
    {
        MiscContract contract = MiscContract.Discover();
        string path = WriteFixture("Undo", JsonValue.Create(true), includePositionTrack: false);
        try
        {
            (EditorSession session, LevelDocument document) = OpenSession(path);
            object before = contract.Get(session);
            contract.Edit(session, before);
            if (session.CanUndo || session.IsDirty)
                throw new InvalidOperationException("no-op Misc Settings edit added history or dirtied the session.");

            object after = contract.Create(false);
            contract.Edit(session, after);
            if (!session.CanUndo || !session.IsDirty)
                throw new InvalidOperationException("one Misc Settings edit was not one dirty undo unit.");
            if (!string.Equals(session.UndoName, "Edit misc settings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"unexpected undo name '{session.UndoName ?? "<null>"}'.");
            AssertThreeLayers(session, document, contract, false, "edit");

            session.Undo();
            if (session.IsDirty || session.CanUndo || !session.CanRedo)
                throw new InvalidOperationException("undo did not restore the clean pre-edit state.");
            AssertThreeLayers(session, document, contract, true, "undo");

            session.Redo();
            if (!session.IsDirty || !session.CanUndo || session.CanRedo)
                throw new InvalidOperationException("redo did not restore the Misc Settings command.");
            AssertThreeLayers(session, document, contract, false, "redo");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySavePreservation()
    {
        MiscContract contract = MiscContract.Discover();
        string path = WriteFixture("Save", JsonValue.Create(true), includePositionTrack: true);
        string savedPath = Path.ChangeExtension(path, ".saved.adofai");
        try
        {
            JsonObject original = LooseAdoFaiJson.ParseObject(path);
            (EditorSession session, _) = OpenSession(path);
            contract.Edit(session, contract.Create(false));
            session.SaveAsync(savedPath).GetAwaiter().GetResult();

            JsonObject saved = LooseAdoFaiJson.ParseObject(savedPath);
            JsonObject savedSettings = saved["settings"]?.AsObject()
                ?? throw new InvalidOperationException("saved source lost settings.");
            if (savedSettings["stickToFloors"]?.GetValue<bool>() != false)
                throw new InvalidOperationException("saved source did not update settings.stickToFloors.");

            string[] preservedSettings =
            [
                "floorIconOutlines", "planetEase", "planetEaseParts", "planetEasePartBehavior",
                "bgVideo", "loopVideo", "vidOffset", "defaultTextColor", "defaultTextShadowColor",
                "congratsText", "perfectText", "customClass",
                "backgroundColor", "bgImage", "parallax", "bgDisplayMode", "lockRot", "loopBG", "scalingRatio",
                "songFilename", "bpm", "volume", "offset", "pitch", "hitsound", "hitsoundVolume",
                "relativeTo", "position", "rotation", "zoom",
                "trackColor", "secondaryTrackColor", "trackColorType", "trackColorAnimDuration",
                "trackColorPulse", "trackPulseLength", "trackStyle", "trackGlowIntensity",
                "song", "artist", "author", "levelDesc", "levelTags", "artistLinks",
                "legacyFlash", "legacyCamRelativeTo", "legacySpriteTiles", "futureSetting"
            ];
            foreach (string key in preservedSettings)
            {
                if (!JsonNode.DeepEquals(original["settings"]?[key], savedSettings[key]))
                    throw new InvalidOperationException($"saving Misc Settings changed preserved settings.{key}.");
            }

            foreach (string key in new[] { "futureRoot", "actions", "decorations" })
            {
                if (!JsonNode.DeepEquals(original[key], saved[key]))
                    throw new InvalidOperationException($"saving Misc Settings changed preserved root {key}.");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(savedPath);
        }
    }

    private static void VerifyRendererFacingState()
    {
        string rootTrue = WriteFixture("Runtime-True", JsonValue.Create(true), includePositionTrack: false);
        string rootFalse = WriteFixture("Runtime-False", JsonValue.Create(false), includePositionTrack: false);
        string overridden = WriteFixture("Runtime-Override", JsonValue.Create(false), includePositionTrack: true);
        try
        {
            (_, LevelDocument trueDocument) = OpenSession(rootTrue);
            (_, LevelDocument falseDocument) = OpenSession(rootFalse);
            (_, LevelDocument overrideDocument) = OpenSession(overridden);

            AssertAllStick(TrackTransformResolver.ResolveStatic(trueDocument), true, "root true");
            AssertAllStick(TrackTransformResolver.ResolveStatic(falseDocument), false, "root false");

            StaticTrackTransform[] resolved = TrackTransformResolver.ResolveStatic(overrideDocument);
            if (resolved.Length < 3 || resolved[0].StickToFloors || !resolved[1].StickToFloors || !resolved[2].StickToFloors)
                throw new InvalidOperationException("PositionTrack persistent override did not supersede root false from its floor onward.");

            NativeFloor[] trueFloors = FlatNativeLevelSnapshotBuilder.BuildProfiled(trueDocument).Snapshot.Floors;
            NativeFloor[] falseFloors = FlatNativeLevelSnapshotBuilder.BuildProfiled(falseDocument).Snapshot.Floors;
            if (trueFloors.Any(floor => (floor.TrackTransformFlags & NativeFloor.TransformFlagStickToFloors) == 0u))
                throw new InvalidOperationException("root true was lost before the renderer-facing NativeFloor snapshot.");
            if (falseFloors.Any(floor => (floor.TrackTransformFlags & NativeFloor.TransformFlagStickToFloors) != 0u))
                throw new InvalidOperationException("root false was lost before the renderer-facing NativeFloor snapshot.");
        }
        finally
        {
            File.Delete(rootTrue);
            File.Delete(rootFalse);
            File.Delete(overridden);
        }
    }

    private static void VerifyRuntimeEditPropagation()
    {
        MiscContract contract = MiscContract.Discover();
        string path = WriteFixture("RuntimeEdit", JsonValue.Create(true), includePositionTrack: false);
        try
        {
            (EditorSession session, LevelDocument document) = OpenSession(path);
            contract.Edit(session, contract.Create(false));
            AssertAllStick(TrackTransformResolver.ResolveStatic(document), false, "edited cache/runtime");
            session.Undo();
            AssertAllStick(TrackTransformResolver.ResolveStatic(document), true, "undo cache/runtime");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyUnsupportedBoundary()
    {
        MiscContract contract = MiscContract.Discover();
        string path = WriteFixture("Boundary", JsonValue.Create(true), includePositionTrack: true);
        try
        {
            (EditorSession session, _) = OpenSession(path);
            JsonObject before = session.GetSourceRootForSave();
            contract.Edit(session, contract.Create(false));
            JsonObject after = session.GetSourceRootForSave();

            string[] unsupported =
            [
                "floorIconOutlines", "planetEase", "planetEaseParts", "planetEasePartBehavior",
                "bgVideo", "loopVideo", "vidOffset", "customClass", "defaultTextColor",
                "defaultTextShadowColor", "congratsText", "perfectText",
                "backgroundColor", "bgImage", "parallax", "bgDisplayMode", "lockRot", "loopBG", "scalingRatio"
            ];
            foreach (string key in unsupported)
            {
                if (!JsonNode.DeepEquals(before["settings"]?[key], after["settings"]?[key]))
                    throw new InvalidOperationException($"Misc Slice 1 changed unsupported setting {key}.");
            }
            if (!JsonNode.DeepEquals(before["actions"], after["actions"]))
                throw new InvalidOperationException("Misc Slice 1 changed PositionTrack action JSON.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySidebarContract()
    {
        XDocument xaml = XDocument.Load(
            Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow.xaml"),
            LoadOptions.PreserveWhitespace);
        XElement root = xaml.Root ?? throw new InvalidOperationException("MainWindow.xaml has no root.");
        XElement sidebar = RequireNamedElement(root, "SettingsSidebarHost");
        XElement inspector = RequireNamedElement(root, "EventInspectorHost");
        foreach (string name in new[]
                 {
                     "SongSettingsCategoryButton", "CameraSettingsCategoryButton", "TrackSettingsCategoryButton",
                     "LevelSettingsCategoryButton", "MiscSettingsCategoryButton",
                     "SongSettingsPane", "CameraSettingsPane", "TrackSettingsPane", "LevelSettingsPane",
                     "MiscSettingsPane", "StickToFloorsCheckBox"
                 })
            _ = RequireNamedElement(root, name);

        if (inspector.AncestorsAndSelf().Contains(sidebar) || sidebar.AncestorsAndSelf().Contains(inspector))
            throw new InvalidOperationException("Misc Settings sidebar interferes with Event Inspector.");

        Type window = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.MainWindow")
            ?? throw new InvalidOperationException("MainWindow type is missing.");
        FieldInfo? state = window.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType.IsEnum &&
                new[] { "Song", "Camera", "Track", "Level", "Misc" }.All(value =>
                    Enum.GetNames(field.FieldType).Contains(value, StringComparer.Ordinal)));
        if (state is null)
            throw new InvalidOperationException("Song/Camera/Track/Level/Misc are not one exclusive enum state.");

        string source = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow"),
                    "MainWindow*.cs")
                .Select(File.ReadAllText));
        if (!source.Contains("MiscSettingsPane.Visibility", StringComparison.Ordinal) ||
            !source.Contains("_settingsPaneCollapsed", StringComparison.Ordinal) ||
            !source.Contains("_refreshingMiscSettings", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("category/collapse/refresh state does not include Misc Settings.");
        }
    }

    private static (EditorSession Session, LevelDocument Document) OpenSession(string path)
    {
        LevelDocument document = AdoFaiLoader.Load(path).Document;
        TrackTransformSourceData transforms = TrackTransformSourceReader.Load(path);
        TrackTransformMetadataCache.Attach(document, transforms);
        return (new EditorSession(document), document);
    }

    private static void AssertThreeLayers(
        EditorSession session,
        LevelDocument document,
        MiscContract contract,
        bool expected,
        string phase)
    {
        if (contract.Read(contract.Get(session)) != expected)
            throw new InvalidOperationException($"{phase}: snapshot mismatch.");
        if (TrackTransformMetadataCache.Get(document).DefaultStickToFloors != expected)
            throw new InvalidOperationException($"{phase}: TrackTransformMetadataCache mismatch.");
        JsonObject settings = session.GetSourceRootForSave()["settings"]?.AsObject()
            ?? throw new InvalidOperationException($"{phase}: source root lost settings.");
        if (settings["stickToFloors"]?.GetValue<bool>() != expected)
            throw new InvalidOperationException($"{phase}: source settings mismatch.");
    }

    private static void AssertAllStick(StaticTrackTransform[] transforms, bool expected, string label)
    {
        if (transforms.Length == 0 || transforms.Any(transform => transform.StickToFloors != expected))
            throw new InvalidOperationException($"{label}: renderer-facing stick state did not equal {expected} on every floor.");
    }

    private static string WriteFixture(string name, JsonNode? stickToFloors, bool includePositionTrack)
    {
        var settings = new JsonObject
        {
            ["songFilename"] = "song.ogg", ["bpm"] = 120, ["volume"] = 80,
            ["offset"] = 25, ["pitch"] = 100, ["hitsound"] = "Kick", ["hitsoundVolume"] = 70,
            ["relativeTo"] = "Player", ["position"] = new JsonArray(1, 2), ["rotation"] = 15, ["zoom"] = 110,
            ["trackColor"] = "112233", ["secondaryTrackColor"] = "aabbcc", ["trackColorType"] = "Glow",
            ["trackColorAnimDuration"] = 3.5, ["trackColorPulse"] = "Forward", ["trackPulseLength"] = 7,
            ["trackStyle"] = "Neon", ["trackGlowIntensity"] = 42.5,
            ["song"] = "Title", ["artist"] = "Artist", ["author"] = "Mapper",
            ["levelDesc"] = "Description", ["levelTags"] = "tags", ["artistLinks"] = "link",
            ["floorIconOutlines"] = true, ["planetEase"] = "InOutSine", ["planetEaseParts"] = 3,
            ["planetEasePartBehavior"] = "Repeat", ["bgVideo"] = "background.mp4", ["loopVideo"] = true,
            ["vidOffset"] = 125, ["customClass"] = "custom", ["defaultTextColor"] = "abcdef",
            ["defaultTextShadowColor"] = "12345678", ["congratsText"] = "Congrats", ["perfectText"] = "Perfect",
            ["backgroundColor"] = "010203", ["bgImage"] = "background.png", ["parallax"] = new JsonArray(80, 90),
            ["bgDisplayMode"] = "Tiled", ["lockRot"] = true, ["loopBG"] = true, ["scalingRatio"] = 125,
            ["legacyFlash"] = true, ["legacyCamRelativeTo"] = false, ["legacySpriteTiles"] = true,
            ["futureSetting"] = new JsonObject { ["keep"] = true }
        };
        if (stickToFloors is not null)
            settings["stickToFloors"] = stickToFloors.DeepClone();

        JsonArray actions = includePositionTrack
            ? new JsonArray(new JsonObject
            {
                ["floor"] = 1, ["eventType"] = "PositionTrack", ["active"] = true,
                ["stickToFloors"] = "Enabled", ["futureAction"] = "keep"
            })
            : new JsonArray();
        var root = new JsonObject
        {
            ["angleData"] = new JsonArray(0, 90, 180), ["settings"] = settings,
            ["actions"] = actions,
            ["decorations"] = new JsonArray(new JsonObject
            {
                ["floor"] = 1, ["eventType"] = "AddDecoration", ["tag"] = "keep"
            }),
            ["futureRoot"] = new JsonObject { ["keep"] = true }
        };
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-MiscSettings-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static XElement RequireNamedElement(XElement root, string name) =>
        root.DescendantsAndSelf().FirstOrDefault(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == name))
        ?? throw new InvalidOperationException($"MainWindow.xaml is missing {name}.");

    private static void Check(List<string> failures, string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { failures.Add($"{name}: {Unwrap(ex).Message}"); }
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception;
    }

    private sealed class MiscContract
    {
        private readonly Type _snapshotType;
        private readonly ConstructorInfo _constructor;
        private readonly PropertyInfo _stickToFloors;
        private readonly MethodInfo _get;
        private readonly MethodInfo _edit;

        private MiscContract(
            Type snapshotType,
            ConstructorInfo constructor,
            PropertyInfo stickToFloors,
            MethodInfo get,
            MethodInfo edit)
        {
            _snapshotType = snapshotType;
            _constructor = constructor;
            _stickToFloors = stickToFloors;
            _get = get;
            _edit = edit;
        }

        internal static MiscContract Discover()
        {
            Assembly assembly = typeof(EditorSession).Assembly;
            Type snapshot = assembly.GetType(SnapshotTypeName)
                ?? throw new InvalidOperationException("typed MiscSettingsSnapshot is missing.");
            PropertyInfo property = snapshot.GetProperty(
                "StickToFloors",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MiscSettingsSnapshot.StickToFloors is missing.");
            if (property.PropertyType != typeof(bool))
                throw new InvalidOperationException("MiscSettingsSnapshot.StickToFloors must be bool.");
            ConstructorInfo constructor = snapshot.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(bool)],
                modifiers: null)
                ?? throw new InvalidOperationException("MiscSettingsSnapshot(bool) is missing.");
            MethodInfo get = typeof(EditorSession).GetMethod(
                "GetMiscSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.GetMiscSettings() is missing.");
            MethodInfo edit = typeof(EditorSession).GetMethod(
                "EditMiscSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [snapshot],
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.EditMiscSettings(snapshot) is missing.");
            MethodInfo applyRaw = typeof(EditorSession).GetMethod(
                "ApplyMiscSettingsRaw",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [snapshot],
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.ApplyMiscSettingsRaw(snapshot) is missing.");
            _ = applyRaw;
            return new MiscContract(snapshot, constructor, property, get, edit);
        }

        internal object Create(bool stickToFloors) => _constructor.Invoke([stickToFloors]);

        internal object Get(EditorSession session) =>
            _get.Invoke(session, null)
            ?? throw new InvalidOperationException("EditorSession.GetMiscSettings returned null.");

        internal void Edit(EditorSession session, object snapshot) => _edit.Invoke(session, [snapshot]);

        internal bool Read(object snapshot)
        {
            if (!_snapshotType.IsInstanceOfType(snapshot))
                throw new InvalidOperationException("unexpected Misc Settings snapshot type.");
            return (bool)(_stickToFloors.GetValue(snapshot)
                ?? throw new InvalidOperationException("MiscSettingsSnapshot.StickToFloors returned null."));
        }
    }
}
