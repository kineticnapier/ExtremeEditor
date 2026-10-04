using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class LevelMetadataSettingsSliceRegression
{
    private const string SnapshotTypeName = "ExtremeEditor.Wpf.LevelMetadataSettingsSnapshot";

    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "typed Level Metadata API", VerifyTypedApiAndLoad);
        Check(failures, "missing and explicit-empty presence", VerifyPresenceSemantics);
        Check(failures, "edit, undo, redo, and dirty state", VerifyEditUndoRedo);
        Check(failures, "save ownership and round-trip preservation", VerifySaveOwnership);
        Check(failures, "runtime non-interference", VerifyRuntimeNonInterference);
        Check(failures, "four-category sidebar", VerifySidebarContract);
        Check(failures, "multiline metadata editing", VerifyMultilineContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Level Metadata Settings Slice is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyTypedApiAndLoad()
    {
        MetadataContract contract = MetadataContract.Discover();
        string path = WriteFixture("Typed", artistNode: JsonValue.Create(""));
        try
        {
            EditorSession session = OpenSession(path, out _);
            object snapshot = contract.Get(session);
            AssertSnapshot(contract, snapshot, new MetadataValues(
                "Display title", "", "Mapper", "Line one\r\nLine two",
                "tag one tag-two", "https://artist.example\nhttps://social.example"), "loaded snapshot");

            JsonObject settings = session.GetSourceRootForSave()["settings"]!.AsObject();
            AssertString(settings, "song", "Display title");
            AssertString(settings, "songFilename", "audio.ogg");
            if (string.Equals(contract.Read(snapshot, "Song"), "audio.ogg", StringComparison.Ordinal))
                throw new InvalidOperationException("song was confused with Song Settings songFilename.");

            Type? oldSongSnapshot = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.LevelSettingsSnapshot");
            if (oldSongSnapshot is null || oldSongSnapshot == contract.SnapshotType)
                throw new InvalidOperationException("Level Metadata reused the existing Song Settings snapshot type.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyPresenceSemantics()
    {
        MetadataContract contract = MetadataContract.Discover();
        string path = WriteFixture("Presence", artistNode: null);
        try
        {
            EditorSession session = OpenSession(path, out _);
            object missing = contract.Get(session);
            if (contract.Read(missing, "Artist") is not null)
                throw new InvalidOperationException("missing artist must be represented as null.");

            object emptyArtist = contract.Create(new MetadataValues(
                "Display title", "", "Mapper", "Line one\r\nLine two",
                "tag one tag-two", "https://artist.example\nhttps://social.example"));
            contract.Edit(session, emptyArtist);
            object after = contract.Get(session);
            if (contract.Read(after, "Artist") is not string value || value.Length != 0)
                throw new InvalidOperationException("explicit empty artist was collapsed to missing/null.");
            JsonObject settings = session.GetSourceRootForSave()["settings"]!.AsObject();
            if (!settings.ContainsKey("artist") || settings["artist"]?.GetValue<string>() != "")
                throw new InvalidOperationException("explicit empty artist presence was not retained in source settings.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyEditUndoRedo()
    {
        MetadataContract contract = MetadataContract.Discover();
        string path = WriteFixture("Undo", artistNode: null);
        try
        {
            EditorSession session = OpenSession(path, out _);
            object before = contract.Get(session);
            contract.Edit(session, before);
            if (session.CanUndo || session.IsDirty)
                throw new InvalidOperationException("no-op metadata edit added history or dirtied the session.");

            var changed = new MetadataValues(
                "Changed title", "", "New mapper", "  preserved description  \nnext line",
                " tags stay raw ", "first\nsecond");
            object after = contract.Create(changed);
            contract.Edit(session, after);
            if (!session.CanUndo || !session.IsDirty)
                throw new InvalidOperationException("one metadata edit was not one dirty undo unit.");
            if (!string.Equals(session.UndoName, "Edit level metadata settings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"unexpected undo name '{session.UndoName ?? "<null>"}'.");
            AssertSnapshot(contract, contract.Get(session), changed, "edited snapshot");
            AssertSource(session, changed, "edited source");

            session.Undo();
            if (session.IsDirty || session.CanUndo || !session.CanRedo)
                throw new InvalidOperationException("undo did not restore clean single-command history state.");
            AssertSnapshot(contract, contract.Get(session), new MetadataValues(
                "Display title", null, "Mapper", "Line one\r\nLine two",
                "tag one tag-two", "https://artist.example\nhttps://social.example"), "undo snapshot");
            JsonObject undoSettings = session.GetSourceRootForSave()["settings"]!.AsObject();
            if (undoSettings.ContainsKey("artist"))
                throw new InvalidOperationException("undo did not restore missing artist property presence.");

            session.Redo();
            if (!session.IsDirty || !session.CanUndo || session.CanRedo)
                throw new InvalidOperationException("redo did not restore the metadata command.");
            AssertSnapshot(contract, contract.Get(session), changed, "redo snapshot");

            object remove = contract.Create(changed with { Author = null });
            contract.Edit(session, remove);
            if (session.GetSourceRootForSave()["settings"]!.AsObject().ContainsKey("author"))
                throw new InvalidOperationException("typed null did not remove the metadata property.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySaveOwnership()
    {
        MetadataContract contract = MetadataContract.Discover();
        string path = WriteFixture("Save", artistNode: JsonValue.Create("Artist"));
        string savedPath = Path.ChangeExtension(path, ".saved.adofai");
        try
        {
            JsonObject original = LooseAdoFaiJson.ParseObject(path);
            EditorSession session = OpenSession(path, out _);
            var changed = new MetadataValues(
                "Saved title", "Saved artist", "Saved mapper", "saved\ndescription",
                "saved tags", "saved link\nsecond link");
            contract.Edit(session, contract.Create(changed));
            session.SaveAsync(savedPath).GetAwaiter().GetResult();
            JsonObject saved = LooseAdoFaiJson.ParseObject(savedPath);
            AssertSource(saved["settings"]!.AsObject(), changed, "saved metadata");

            string[] preservedSettings =
            {
                "songFilename", "bpm", "volume", "offset", "pitch", "hitsound", "hitsoundVolume",
                "relativeTo", "position", "rotation", "zoom",
                "trackColor", "secondaryTrackColor", "trackColorType", "trackColorAnimDuration",
                "trackColorPulse", "trackPulseLength", "trackStyle", "trackGlowIntensity",
                "floorIconOutlines", "stickToFloors", "separateCountdownTime",
                "specialArtistType", "artistPermission", "previewImage", "previewIcon",
                "previewIconColor", "previewSongStart", "previewSongDuration", "seizureWarning",
                "speedTrialAim", "difficulty", "requiredMods", "futureSetting"
            };
            foreach (string key in preservedSettings)
            {
                if (!JsonNode.DeepEquals(original["settings"]?[key], saved["settings"]?[key]))
                    throw new InvalidOperationException($"metadata save changed preserved settings.{key}.");
            }
            foreach (string key in new[] { "futureRoot", "actions", "decorations" })
            {
                if (!JsonNode.DeepEquals(original[key], saved[key]))
                    throw new InvalidOperationException($"metadata save changed preserved root {key}.");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(savedPath);
        }
    }

    private static void VerifyRuntimeNonInterference()
    {
        MetadataContract contract = MetadataContract.Discover();
        string path = WriteFixture("Runtime", artistNode: JsonValue.Create("Artist"));
        try
        {
            EditorSession session = OpenSession(path, out LevelDocument document);
            double bpm = document.InitialBpm;
            double offset = document.OffsetMilliseconds;
            double pitch = document.PitchPercent;
            double[] angles = document.Angles.ToArray();
            LevelAction[] actions = document.ActionStore.Actions.ToArray();
            LevelCameraSettings camera = document.CameraSettings;
            TrackVisualSourceData track = TrackVisualMetadataCache.Get(document);
            JsonArray actionJson = (JsonArray)session.GetSourceRootForSave()["actions"]!.DeepClone();

            contract.Edit(session, contract.Create(new MetadataValues(
                "Metadata only", "Other artist", "Other mapper", "desc", "tags", "links")));

            if (document.InitialBpm != bpm || document.OffsetMilliseconds != offset || document.PitchPercent != pitch)
                throw new InvalidOperationException("metadata edit changed timing/audio fields.");
            if (!document.Angles.SequenceEqual(angles) || !document.ActionStore.Actions.SequenceEqual(actions))
                throw new InvalidOperationException("metadata edit changed floor geometry or action store.");
            if (!ReferenceEquals(camera, document.CameraSettings))
                throw new InvalidOperationException("metadata edit replaced camera metadata.");
            if (!ReferenceEquals(track, TrackVisualMetadataCache.Get(document)))
                throw new InvalidOperationException("metadata edit replaced track metadata.");
            if (!JsonNode.DeepEquals(actionJson, session.GetSourceRootForSave()["actions"]))
                throw new InvalidOperationException("metadata edit changed serialized actions.");
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
                     "LevelSettingsCategoryButton", "SongSettingsPane", "CameraSettingsPane", "TrackSettingsPane",
                     "LevelSettingsPane", "LevelSongTitleTextBox", "LevelArtistTextBox", "LevelAuthorTextBox",
                     "LevelDescriptionTextBox", "LevelTagsTextBox", "LevelArtistLinksTextBox"
                 })
            _ = RequireNamedElement(root, name);
        if (inspector.AncestorsAndSelf().Contains(sidebar) || sidebar.AncestorsAndSelf().Contains(inspector))
            throw new InvalidOperationException("Level Settings sidebar interferes with Event Inspector.");

        Type window = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.MainWindow")!;
        FieldInfo? state = window.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType.IsEnum &&
                new[] { "Song", "Camera", "Track", "Level" }.All(value =>
                    Enum.GetNames(field.FieldType).Contains(value, StringComparer.Ordinal)));
        if (state is null)
            throw new InvalidOperationException("Song/Camera/Track/Level are not one exclusive enum state.");

        string source = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow"), "MainWindow*.cs")
                .Select(File.ReadAllText));
        if (!source.Contains("LevelSettingsPane.Visibility", StringComparison.Ordinal) ||
            !source.Contains("_settingsPaneCollapsed", StringComparison.Ordinal))
            throw new InvalidOperationException("collapse/category state does not include LevelSettingsPane.");
    }

    private static void VerifyMultilineContract()
    {
        XDocument xaml = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow.xaml"));
        XElement root = xaml.Root!;
        XElement description = RequireNamedElement(root, "LevelDescriptionTextBox");
        if (!IsTrue(description, "AcceptsReturn") || !IsTrue(description, "TextWrapping"))
            throw new InvalidOperationException("levelDesc is not configured as multiline input.");
        if (description.Attributes().Any(attribute => attribute.Name.LocalName == "PreviewKeyDown"))
            throw new InvalidOperationException("levelDesc incorrectly uses the single-line Enter commit handler.");
        if (!description.Attributes().Any(attribute => attribute.Name.LocalName == "LostKeyboardFocus"))
            throw new InvalidOperationException("levelDesc does not commit once on focus loss.");
    }

    private static bool IsTrue(XElement element, string attribute) =>
        string.Equals(element.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value, "True", StringComparison.OrdinalIgnoreCase) ||
        (attribute == "TextWrapping" && string.Equals(element.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value, "Wrap", StringComparison.OrdinalIgnoreCase));

    private static EditorSession OpenSession(string path, out LevelDocument document)
    {
        document = AdoFaiLoader.Load(path).Document;
        TrackVisualSourceBundle visual = TrackVisualSourceReader.Load(path);
        TrackVisualMetadataCache.Attach(document, visual.Visual);
        TrackColorMetadataCache.Attach(document, visual.Legacy);
        return new EditorSession(document);
    }

    private static string WriteFixture(string name, JsonNode? artistNode)
    {
        var settings = new JsonObject
        {
            ["song"] = "Display title", ["songFilename"] = "audio.ogg", ["author"] = "Mapper",
            ["levelDesc"] = "Line one\r\nLine two", ["levelTags"] = "tag one tag-two",
            ["artistLinks"] = "https://artist.example\nhttps://social.example",
            ["bpm"] = 123.0, ["volume"] = 71.0, ["offset"] = 45.0, ["pitch"] = 125.0,
            ["hitsound"] = "Hat", ["hitsoundVolume"] = 63.0,
            ["relativeTo"] = "Global", ["position"] = new JsonArray(2.0, 3.0), ["rotation"] = 12.0, ["zoom"] = 88.0,
            ["trackColor"] = "123456", ["secondaryTrackColor"] = "abcdef", ["trackColorType"] = "Glow",
            ["trackColorAnimDuration"] = 3.0, ["trackColorPulse"] = "Forward", ["trackPulseLength"] = 6,
            ["trackStyle"] = "Neon", ["trackGlowIntensity"] = 42.0,
            ["floorIconOutlines"] = true, ["stickToFloors"] = false, ["separateCountdownTime"] = true,
            ["specialArtistType"] = "PublicLicense", ["artistPermission"] = "permission.png",
            ["previewImage"] = "preview.png", ["previewIcon"] = "icon.png", ["previewIconColor"] = "003f52",
            ["previewSongStart"] = 5, ["previewSongDuration"] = 12, ["seizureWarning"] = true,
            ["speedTrialAim"] = 1.25, ["difficulty"] = 7, ["requiredMods"] = new JsonArray("mod-a"),
            ["futureSetting"] = new JsonObject { ["nested"] = 9 }
        };
        if (artistNode is not null)
            settings["artist"] = artistNode;
        var root = new JsonObject
        {
            ["angleData"] = new JsonArray(0, 90, 180), ["settings"] = settings,
            ["actions"] = new JsonArray(new JsonObject { ["floor"] = 1, ["eventType"] = "Twirl", ["active"] = true, ["futureAction"] = 4 }),
            ["decorations"] = new JsonArray(new JsonObject { ["floor"] = 1, ["eventType"] = "AddDecoration", ["tag"] = "keep" }),
            ["futureRoot"] = new JsonObject { ["keep"] = true }
        };
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-LevelMetadata-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static void AssertSource(EditorSession session, MetadataValues expected, string label) =>
        AssertSource(session.GetSourceRootForSave()["settings"]!.AsObject(), expected, label);

    private static void AssertSource(JsonObject settings, MetadataValues expected, string label)
    {
        foreach ((string key, string? value) in expected.AsPairs())
        {
            if (value is null)
            {
                if (settings.ContainsKey(key))
                    throw new InvalidOperationException($"{label}: {key} should be missing.");
            }
            else
                AssertString(settings, key, value);
        }
    }

    private static void AssertSnapshot(MetadataContract contract, object snapshot, MetadataValues expected, string label)
    {
        foreach ((string property, string? value) in expected.AsProperties())
        {
            if (!string.Equals(contract.Read(snapshot, property), value, StringComparison.Ordinal))
                throw new InvalidOperationException($"{label}.{property}: expected '{value ?? "<null>"}', actual '{contract.Read(snapshot, property) ?? "<null>"}'.");
        }
    }

    private static void AssertString(JsonObject obj, string key, string expected)
    {
        if (obj[key]?.GetValue<string>() != expected)
            throw new InvalidOperationException($"settings.{key}: expected '{expected}'.");
    }

    private static XElement RequireNamedElement(XElement root, string name)
    {
        XElement? element = root.DescendantsAndSelf().FirstOrDefault(candidate => candidate.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name));
        return element ?? throw new InvalidOperationException($"MainWindow.xaml is missing {name}.");
    }

    private static void Check(List<string> failures, string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { failures.Add($"{name}: {Unwrap(ex).Message}"); }
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception;
    }

    private sealed record MetadataValues(string? Song, string? Artist, string? Author, string? LevelDesc, string? LevelTags, string? ArtistLinks)
    {
        internal IEnumerable<(string Key, string? Value)> AsPairs()
        {
            yield return ("song", Song); yield return ("artist", Artist); yield return ("author", Author);
            yield return ("levelDesc", LevelDesc); yield return ("levelTags", LevelTags); yield return ("artistLinks", ArtistLinks);
        }
        internal IEnumerable<(string Property, string? Value)> AsProperties()
        {
            yield return ("Song", Song); yield return ("Artist", Artist); yield return ("Author", Author);
            yield return ("LevelDesc", LevelDesc); yield return ("LevelTags", LevelTags); yield return ("ArtistLinks", ArtistLinks);
        }
    }

    private sealed class MetadataContract
    {
        private MetadataContract(
            Type snapshotType,
            ConstructorInfo constructor,
            MethodInfo getMethod,
            MethodInfo editMethod)
        {
            SnapshotType = snapshotType;
            Constructor = constructor;
            GetMethod = getMethod;
            EditMethod = editMethod;
        }

        internal Type SnapshotType { get; }
        private ConstructorInfo Constructor { get; }
        private MethodInfo GetMethod { get; }
        private MethodInfo EditMethod { get; }

        internal static MetadataContract Discover()
        {
            Assembly assembly = typeof(EditorSession).Assembly;
            Type snapshot = assembly.GetType(SnapshotTypeName)
                ?? throw new InvalidOperationException("LevelMetadataSettingsSnapshot is missing.");
            ConstructorInfo constructor = snapshot.GetConstructors().SingleOrDefault(candidate => candidate.GetParameters().Length == 6)
                ?? throw new InvalidOperationException("LevelMetadataSettingsSnapshot must expose six nullable string values.");
            MethodInfo get = typeof(EditorSession).GetMethod("GetLevelMetadataSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EditorSession.GetLevelMetadataSettings() is missing.");
            MethodInfo edit = typeof(EditorSession).GetMethod("EditLevelMetadataSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EditorSession.EditLevelMetadataSettings(...) is missing.");
            MethodInfo? apply = typeof(EditorSession).GetMethod("ApplyLevelMetadataSettingsRaw", BindingFlags.Instance | BindingFlags.NonPublic);
            if (apply is null)
                throw new InvalidOperationException("EditorSession.ApplyLevelMetadataSettingsRaw(...) is missing.");
            if (!typeof(EditorSession).GetNestedTypes(BindingFlags.NonPublic).Any(type => type.Name.Contains("EditLevelMetadataSettingsCommand", StringComparison.Ordinal)))
                throw new InvalidOperationException("EditLevelMetadataSettingsCommand is missing.");
            return new MetadataContract(snapshot, constructor, get, edit);
        }

        internal object Create(MetadataValues value) => Constructor.Invoke(new object?[]
            { value.Song, value.Artist, value.Author, value.LevelDesc, value.LevelTags, value.ArtistLinks });
        internal object Get(EditorSession session) => GetMethod.Invoke(session, null)!;
        internal void Edit(EditorSession session, object snapshot) => EditMethod.Invoke(session, new[] { snapshot });
        internal string? Read(object snapshot, string property) =>
            SnapshotType.GetProperty(property)?.GetValue(snapshot) as string
            ?? (SnapshotType.GetProperty(property) is null ? throw new InvalidOperationException($"Snapshot.{property} is missing.") : null);
    }
}
