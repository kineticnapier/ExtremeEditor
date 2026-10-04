using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TrackSettingsSliceRegression
{
    private const string SnapshotTypeName = "ExtremeEditor.Wpf.TrackSettingsSnapshot";

    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "root settings load", VerifyRootSettingsLoad);
        Check(failures, "reader defaults", VerifyReaderDefaults);
        Check(failures, "typed snapshot contract", VerifyTypedSnapshotContract);
        Check(failures, "edit/undo three-layer coherence", VerifyEditUndoCoherence);
        Check(failures, "save ownership and preservation", VerifySavePreservation);
        Check(failures, "renderer-facing state", VerifyRendererFacingState);
        Check(failures, "Recolor fallback state", VerifyRecolorFallbackState);
        Check(failures, "unsupported settings boundary", VerifyUnsupportedBoundary);
        Check(failures, "three-category sidebar state", VerifySidebarContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Track Settings Slice is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyRootSettingsLoad()
    {
        string path = WriteFixture("Load", includeRecolor: false);
        try
        {
            TrackVisualStyle style = TrackVisualSourceReader.Load(path).Visual.InitialStyle;
            AssertStyle(
                style,
                "Glow", "112233", "aabbcc", 3.5,
                "Forward", 7, "Neon", 42.5,
                "root settings load");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyReaderDefaults()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-TrackSettings-Defaults-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, """
        {
          "angleData": [0, 90],
          "settings": { "bpm": 100 },
          "actions": [],
          "decorations": []
        }
        """);
        try
        {
            TrackVisualStyle style = TrackVisualSourceReader.Load(path).Visual.InitialStyle;
            AssertStyle(
                style,
                "Single", "debb7b", "ffffff", 2.0,
                "None", 10, "Standard", 100.0,
                "reader defaults");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyTypedSnapshotContract()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("Typed", includeRecolor: false);
        try
        {
            (EditorSession session, _, _) = OpenSession(path);
            object snapshot = contract.Get(session);
            AssertSnapshot(
                contract,
                snapshot,
                "Glow", "112233", "aabbcc", 3.5,
                "Forward", 7, "Neon", 42.5,
                "typed snapshot");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyEditUndoCoherence()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("Undo", includeRecolor: true);
        try
        {
            (EditorSession session, LevelDocument document, TrackVisualSourceData originalMetadata) = OpenSession(path);
            object before = contract.Get(session);

            contract.Edit(session, before);
            if (session.CanUndo || session.IsDirty)
                throw new InvalidOperationException("no-op Track Settings edit added history or dirtied the session.");

            object after = contract.Create(
                "Switch", "445566", "778899", 4.0,
                "Backward", 8, "Gems", 40.0);
            contract.Edit(session, after);
            if (!session.CanUndo || !session.IsDirty)
                throw new InvalidOperationException("Track Settings edit was not recorded as one dirty undo unit.");
            if (!string.Equals(session.UndoName, "Edit track settings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"unexpected undo name '{session.UndoName ?? "<null>"}'.");
            AssertThreeLayers(session, document, contract, after, originalMetadata.Events, "edit");

            session.Undo();
            if (session.IsDirty || !session.CanRedo || session.CanUndo)
                throw new InvalidOperationException("undo did not restore clean single-command history state.");
            AssertThreeLayers(session, document, contract, before, originalMetadata.Events, "undo");

            session.Redo();
            if (!session.IsDirty || !session.CanUndo || session.CanRedo)
                throw new InvalidOperationException("redo did not restore the single Track Settings command.");
            AssertThreeLayers(session, document, contract, after, originalMetadata.Events, "redo");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySavePreservation()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("Save", includeRecolor: true);
        string savedPath = Path.ChangeExtension(path, ".saved.adofai");
        try
        {
            JsonObject original = LooseAdoFaiJson.ParseObject(path);
            (EditorSession session, _, _) = OpenSession(path);
            object after = contract.Create(
                "Blink", "010203", "f0e0d0", 5.0,
                "None", 12, "Minimal", 25.0);
            contract.Edit(session, after);
            session.SaveAsync(savedPath).GetAwaiter().GetResult();

            JsonObject saved = LooseAdoFaiJson.ParseObject(savedPath);
            JsonObject settings = saved["settings"] as JsonObject
                ?? throw new InvalidOperationException("saved source lost settings.");
            AssertSettingsJson(settings, contract, after, "saved target keys");

            foreach (string key in new[]
                     {
                         "futureTrackSetting", "trackTexture", "trackTextureScale", "tileShape",
                         "trackShadowColor", "trackAnimation", "beatsAhead",
                         "trackDisappearAnimation", "beatsBehind", "floorIconOutlines", "stickToFloors",
                         "songFilename", "bpm", "volume", "offset", "pitch", "hitsound", "hitsoundVolume",
                         "relativeTo", "position", "rotation", "zoom"
                     })
            {
                if (!JsonNode.DeepEquals(original["settings"]?[key], settings[key]))
                    throw new InvalidOperationException($"saving Track Settings changed preserved settings.{key}.");
            }

            if (!JsonNode.DeepEquals(original["actions"], saved["actions"]))
                throw new InvalidOperationException("saving Track Settings changed ColorTrack/RecolorTrack actions.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(savedPath);
        }
    }

    private static void VerifyRendererFacingState()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("Runtime", includeRecolor: false, pitch: 200.0);
        try
        {
            (EditorSession session, LevelDocument document, _) = OpenSession(path);
            object after = contract.Create(
                "Glow", "445566", "778899", 4.0,
                "Backward", 8, "Gems", 40.0);
            contract.Edit(session, after);

            NativeTrackVisual visual = TrackVisualResolver.Resolve(document)[0];
            AssertEqual(0xFF665544u, visual.PrimaryColor, "runtime primary color");
            AssertEqual(0xFF998877u, visual.SecondaryColor, "runtime secondary color");
            AssertEqual(2u, visual.Flags & 7u, "runtime color type");
            AssertEqual(2u, (visual.Flags >> 6) & 3u, "runtime pulse");
            AssertEqual(5u, (visual.Flags >> 3) & 7u, "runtime style");
            AssertEqual(8u, visual.PulseLength, "runtime pulse length");
            AssertNear(0.4, visual.GlowIntensity, "runtime glow intensity");
            AssertNear(2.0, visual.AnimDuration, "runtime raw duration / song.pitch");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyRecolorFallbackState()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("RecolorFallback", includeRecolor: true, pitch: 200.0);
        try
        {
            (EditorSession session, LevelDocument document, _) = OpenSession(path);
            object after = contract.Create(
                "Single", "345678", "abcdef", 6.0,
                "None", 9, "Basic", 30.0);
            contract.Edit(session, after);

            NativeTrackVisualEvent recolor = TrackVisualTimelineBuilder.Build(document).Single();
            AssertEqual(0xFF785634u, recolor.PrimaryColor, "Recolor fallback primary color");
            AssertEqual(0xFFEFCDABu, recolor.SecondaryColor, "Recolor fallback secondary color");
            AssertNear(3.0, recolor.AnimDuration, "Recolor fallback pitch-adjusted duration");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyUnsupportedBoundary()
    {
        TrackSettingsContract contract = TrackSettingsContract.Discover();
        string path = WriteFixture("Unsupported", includeRecolor: false, colorType: "Volume");
        try
        {
            (EditorSession session, LevelDocument document, _) = OpenSession(path);
            object before = contract.Get(session);
            if (!string.Equals(contract.ReadString(before, "TrackColorType"), "Volume", StringComparison.Ordinal))
                throw new InvalidOperationException("trackColorType=Volume was normalized while reading typed state.");

            object after = contract.Create(
                "Volume", "223344", "ccddee", 7.0,
                "Forward", 6, "NeonLight", 55.0);
            contract.Edit(session, after);
            TrackVisualStyle metadata = TrackVisualMetadataCache.Get(document).InitialStyle;
            if (!string.Equals(metadata.ColorType, "Volume", StringComparison.Ordinal))
                throw new InvalidOperationException("trackColorType=Volume was normalized during edit.");

            JsonObject settings = session.GetSourceRootForSave()["settings"] as JsonObject
                ?? throw new InvalidOperationException("source settings are missing.");
            AssertJsonString(settings, "trackTexture", "custom-track.png");
            AssertJsonNumber(settings, "trackTextureScale", 2.5);
            AssertJsonString(settings, "tileShape", "Hexagon");
            AssertJsonString(settings, "trackShadowColor", "12345678");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySidebarContract()
    {
        string xamlPath = Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow.xaml");
        XDocument xaml = XDocument.Load(xamlPath, LoadOptions.PreserveWhitespace);
        XElement root = xaml.Root ?? throw new InvalidOperationException("MainWindow.xaml has no root element.");
        XElement sidebar = RequireNamedElement(root, "SettingsSidebarHost");
        XElement inspector = RequireNamedElement(root, "EventInspectorHost");

        foreach (string name in new[]
                 {
                     "SongSettingsCategoryButton", "CameraSettingsCategoryButton", "TrackSettingsCategoryButton",
                     "SongSettingsPane", "CameraSettingsPane", "TrackSettingsPane",
                     "TrackPrimaryColorTextBox", "TrackSecondaryColorTextBox", "TrackColorTypeComboBox",
                     "TrackColorAnimDurationTextBox", "TrackColorPulseComboBox", "TrackPulseLengthTextBox",
                     "TrackStyleComboBox", "TrackGlowIntensityTextBox"
                 })
        {
            _ = RequireNamedElement(root, name);
        }

        if (inspector.AncestorsAndSelf().Contains(sidebar) || sidebar.AncestorsAndSelf().Contains(inspector))
            throw new InvalidOperationException("Track Settings sidebar must remain independent from Event Inspector.");

        Type window = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.MainWindow")
            ?? throw new InvalidOperationException("MainWindow type is missing.");
        FieldInfo? categoryField = window
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(field =>
                field.FieldType.IsEnum &&
                new[] { "Song", "Camera", "Track" }.All(name =>
                    Enum.GetNames(field.FieldType).Contains(name, StringComparer.Ordinal)));
        if (categoryField is null)
            throw new InvalidOperationException("Song/Camera/Track are not represented by one exclusive enum state.");

        string mainWindowDirectory = Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.Wpf", "MainWindow");
        string stateSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(mainWindowDirectory, "MainWindow*.cs")
                .Select(File.ReadAllText));
        if (!stateSource.Contains("TrackSettingsPane.Visibility", StringComparison.Ordinal) ||
            !stateSource.Contains("_settingsPaneCollapsed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "sidebar collapse/category state does not include TrackSettingsPane alongside Song and Camera panes.");
        }
    }

    private static (EditorSession Session, LevelDocument Document, TrackVisualSourceData Metadata) OpenSession(string path)
    {
        LevelDocument document = AdoFaiLoader.Load(path).Document;
        TrackVisualSourceBundle bundle = TrackVisualSourceReader.Load(path);
        TrackVisualMetadataCache.Attach(document, bundle.Visual);
        TrackColorMetadataCache.Attach(document, bundle.Legacy);
        TrackAnimationMetadataCache.Attach(document, bundle.Animation);
        return (new EditorSession(document), document, bundle.Visual);
    }

    private static void AssertThreeLayers(
        EditorSession session,
        LevelDocument document,
        TrackSettingsContract contract,
        object expected,
        TrackVisualSourceEvent[] expectedEvents,
        string phase)
    {
        AssertSnapshotMatches(contract, expected, contract.Get(session), $"{phase} snapshot");
        TrackVisualSourceData metadata = TrackVisualMetadataCache.Get(document);
        AssertMetadataMatches(contract, expected, metadata.InitialStyle, $"{phase} metadata");
        if (!metadata.Events.SequenceEqual(expectedEvents))
            throw new InvalidOperationException($"{phase} changed ColorTrack/RecolorTrack event metadata.");
        JsonObject settings = session.GetSourceRootForSave()["settings"] as JsonObject
            ?? throw new InvalidOperationException($"{phase} source root lost settings.");
        AssertSettingsJson(settings, contract, expected, $"{phase} source settings");
    }

    private static void AssertSnapshotMatches(
        TrackSettingsContract contract,
        object expected,
        object actual,
        string label)
    {
        foreach (string property in TrackSettingsContract.PropertyNames)
        {
            object? expectedValue = contract.Read(expected, property);
            object? actualValue = contract.Read(actual, property);
            if (expectedValue is double expectedDouble)
                AssertNear(expectedDouble, Convert.ToDouble(actualValue), $"{label} {property}");
            else if (expectedValue is int expectedInt)
                AssertEqual(expectedInt, Convert.ToInt32(actualValue), $"{label} {property}");
            else if (!string.Equals(Convert.ToString(expectedValue), Convert.ToString(actualValue), StringComparison.Ordinal))
                throw new InvalidOperationException($"{label} {property}: expected={expectedValue}, actual={actualValue}.");
        }
    }

    private static void AssertMetadataMatches(
        TrackSettingsContract contract,
        object expected,
        TrackVisualStyle style,
        string label) =>
        AssertStyle(
            style,
            contract.ReadString(expected, "TrackColorType"),
            contract.ReadString(expected, "TrackColor"),
            contract.ReadString(expected, "SecondaryTrackColor"),
            contract.ReadDouble(expected, "TrackColorAnimDuration"),
            contract.ReadString(expected, "TrackColorPulse"),
            contract.ReadInt(expected, "TrackPulseLength"),
            contract.ReadString(expected, "TrackStyle"),
            contract.ReadDouble(expected, "TrackGlowIntensity"),
            label);

    private static void AssertSettingsJson(
        JsonObject settings,
        TrackSettingsContract contract,
        object expected,
        string label)
    {
        AssertJsonString(settings, "trackColor", contract.ReadString(expected, "TrackColor"));
        AssertJsonString(settings, "secondaryTrackColor", contract.ReadString(expected, "SecondaryTrackColor"));
        AssertJsonString(settings, "trackColorType", contract.ReadString(expected, "TrackColorType"));
        AssertJsonNumber(settings, "trackColorAnimDuration", contract.ReadDouble(expected, "TrackColorAnimDuration"));
        AssertJsonString(settings, "trackColorPulse", contract.ReadString(expected, "TrackColorPulse"));
        AssertJsonNumber(settings, "trackPulseLength", contract.ReadInt(expected, "TrackPulseLength"));
        AssertJsonString(settings, "trackStyle", contract.ReadString(expected, "TrackStyle"));
        AssertJsonNumber(settings, "trackGlowIntensity", contract.ReadDouble(expected, "TrackGlowIntensity"));
    }

    private static void AssertSnapshot(
        TrackSettingsContract contract,
        object snapshot,
        string colorType,
        string primary,
        string secondary,
        double duration,
        string pulse,
        int pulseLength,
        string style,
        double glow,
        string label)
    {
        object expected = contract.Create(colorType, primary, secondary, duration, pulse, pulseLength, style, glow);
        AssertSnapshotMatches(contract, expected, snapshot, label);
    }

    private static void AssertStyle(
        TrackVisualStyle actual,
        string colorType,
        string primary,
        string secondary,
        double duration,
        string pulse,
        int pulseLength,
        string style,
        double glow,
        string label)
    {
        AssertString(colorType, actual.ColorType, $"{label} color type");
        AssertString(primary, actual.PrimaryColor, $"{label} primary color");
        AssertString(secondary, actual.SecondaryColor, $"{label} secondary color");
        AssertNear(duration, actual.AnimDuration, $"{label} animation duration");
        AssertString(pulse, actual.PulseType, $"{label} pulse");
        AssertEqual(pulseLength, actual.PulseLength, $"{label} pulse length");
        AssertString(style, actual.TrackStyle, $"{label} style");
        AssertNear(glow, actual.GlowIntensity, $"{label} glow intensity");
    }

    private static string WriteFixture(
        string name,
        bool includeRecolor,
        double pitch = 100.0,
        string colorType = "Glow")
    {
        string actions = includeRecolor
            ? """
              [
                {
                  "floor": 1,
                  "eventType": "ColorTrack",
                  "active": false,
                  "trackColor": "999999",
                  "futureColorActionProperty": "keep-color"
                },
                {
                  "floor": 1,
                  "eventType": "RecolorTrack",
                  "active": true,
                  "duration": 1,
                  "ease": "Linear",
                  "startTile": [0, "ThisTile"],
                  "endTile": [0, "ThisTile"],
                  "futureRecolorProperty": "keep-recolor"
                }
              ]
              """
            : "[]";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-TrackSettings-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, $$"""
        {
          "angleData": [0, 90, 180],
          "settings": {
            "songFilename": "song.ogg",
            "bpm": 120,
            "volume": 80,
            "offset": 25,
            "pitch": {{pitch.ToString(CultureInfo.InvariantCulture)}},
            "hitsound": "Kick",
            "hitsoundVolume": 70,
            "relativeTo": "Player",
            "position": [1, 2],
            "rotation": 15,
            "zoom": 110,
            "trackColorType": "{{colorType}}",
            "trackColor": "112233",
            "secondaryTrackColor": "aabbcc",
            "trackColorAnimDuration": 3.5,
            "trackColorPulse": "Forward",
            "trackPulseLength": 7,
            "trackStyle": "Neon",
            "trackGlowIntensity": 42.5,
            "trackTexture": "custom-track.png",
            "trackTextureScale": 2.5,
            "tileShape": "Hexagon",
            "trackShadowColor": "12345678",
            "trackAnimation": "Grow",
            "beatsAhead": 3,
            "trackDisappearAnimation": "Fade",
            "beatsBehind": 4,
            "floorIconOutlines": true,
            "stickToFloors": false,
            "futureTrackSetting": { "token": "keep-me" }
          },
          "actions": {{actions}},
          "decorations": []
        }
        """);
        return path;
    }

    private static XElement RequireNamedElement(XElement root, string name) =>
        root.DescendantsAndSelf().FirstOrDefault(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                string.Equals(attribute.Value, name, StringComparison.Ordinal)))
        ?? throw new InvalidOperationException($"Track Settings XAML element '{name}' is missing.");

    private static void AssertJsonString(JsonObject obj, string key, string expected)
    {
        string? actual = obj[key]?.GetValue<string>();
        AssertString(expected, actual, $"settings.{key}");
    }

    private static void AssertJsonNumber(JsonObject obj, string key, double expected)
    {
        double actual = double.TryParse(
            obj[key]?.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed
            : double.NaN;
        AssertNear(expected, actual, $"settings.{key}");
    }

    private static void AssertString(string expected, string? actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual ?? "<null>"}.");
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1.0e-6)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }

    private static void AssertEqual<T>(T expected, T actual, string label) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }

    private static void Check(List<string> failures, string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {Unwrap(ex).Message}");
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException invocation && invocation.InnerException is Exception inner)
            ex = inner;
        return ex;
    }

    private sealed class TrackSettingsContract
    {
        internal static readonly string[] PropertyNames =
        [
            "TrackColorType", "TrackColor", "SecondaryTrackColor", "TrackColorAnimDuration",
            "TrackColorPulse", "TrackPulseLength", "TrackStyle", "TrackGlowIntensity"
        ];

        private readonly Type _snapshotType;
        private readonly ConstructorInfo _constructor;
        private readonly MethodInfo _get;
        private readonly MethodInfo _edit;

        private TrackSettingsContract(
            Type snapshotType,
            ConstructorInfo constructor,
            MethodInfo get,
            MethodInfo edit)
        {
            _snapshotType = snapshotType;
            _constructor = constructor;
            _get = get;
            _edit = edit;
        }

        internal static TrackSettingsContract Discover()
        {
            Assembly assembly = typeof(EditorSession).Assembly;
            Type snapshotType = assembly.GetType(SnapshotTypeName)
                ?? throw new InvalidOperationException("typed TrackSettingsSnapshot is missing.");
            foreach (string property in PropertyNames)
            {
                if (snapshotType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null)
                    throw new InvalidOperationException($"TrackSettingsSnapshot.{property} is missing.");
            }

            ConstructorInfo constructor = snapshotType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(string), typeof(string), typeof(double), typeof(string), typeof(int), typeof(string), typeof(double)],
                modifiers: null)
                ?? throw new InvalidOperationException("TrackSettingsSnapshot must retain exactly the eight supported values.");
            MethodInfo get = typeof(EditorSession).GetMethod(
                "GetTrackSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.GetTrackSettings() is missing.");
            MethodInfo edit = typeof(EditorSession).GetMethod(
                "EditTrackSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [snapshotType],
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.EditTrackSettings(snapshot) is missing.");
            return new TrackSettingsContract(snapshotType, constructor, get, edit);
        }

        internal object Create(
            string colorType,
            string trackColor,
            string secondaryTrackColor,
            double animDuration,
            string pulse,
            int pulseLength,
            string style,
            double glow) =>
            _constructor.Invoke(
            [
                colorType, trackColor, secondaryTrackColor, animDuration,
                pulse, pulseLength, style, glow
            ]);

        internal object Get(EditorSession session) =>
            _get.Invoke(session, null)
            ?? throw new InvalidOperationException("EditorSession.GetTrackSettings returned null.");

        internal void Edit(EditorSession session, object snapshot) => _edit.Invoke(session, [snapshot]);

        internal object? Read(object snapshot, string property) =>
            _snapshotType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(snapshot)
            ?? throw new InvalidOperationException($"TrackSettingsSnapshot.{property} is unavailable.");

        internal string ReadString(object snapshot, string property) => Convert.ToString(Read(snapshot, property)) ?? string.Empty;
        internal double ReadDouble(object snapshot, string property) => Convert.ToDouble(Read(snapshot, property), CultureInfo.InvariantCulture);
        internal int ReadInt(object snapshot, string property) => Convert.ToInt32(Read(snapshot, property), CultureInfo.InvariantCulture);
    }
}
