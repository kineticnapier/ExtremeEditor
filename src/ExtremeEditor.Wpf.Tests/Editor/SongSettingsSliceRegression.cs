using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SongSettingsSliceRegression
{
    private const string SnapshotTypeName = "ExtremeEditor.Wpf.LevelSettingsSnapshot";

    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "volume load", VerifyVolumeLoadPaths);
        Check(failures, "typed settings edit/save/undo", VerifySettingsEditSaveAndUndo);
        Check(failures, "BPM SetSpeed reevaluation", VerifyInitialBpmReevaluatesSetSpeed);
        Check(failures, "song volume isolation", VerifySongVolumeContract);
        Check(failures, "offset/pitch refresh data", VerifyOffsetAndPitchUpdate);
        Check(failures, "song filename relative path", VerifySongFilenameRelativePath);
        Check(failures, "default hitsound refresh", VerifyDefaultHitSoundUpdate);
        Check(failures, "settings sidebar shell", VerifySidebarShell);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Song Settings Slice 1 is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyVolumeLoadPaths()
    {
        PropertyInfo volumeProperty = typeof(LevelDocument).GetProperty("SongVolumePercent")
            ?? throw new InvalidOperationException(
                "LevelDocument.SongVolumePercent is missing; stock settings.volume is discarded.");

        string path = WriteFixture("VolumeLoad", volume: 37.5, unknownValue: "volume-load");
        try
        {
            AssertVolume(AdoFaiLoader.Load(path).Document, volumeProperty, 37.5, "normal loader");
            AssertVolume(
                AdoFaiLoader.LoadAsync(path).GetAwaiter().GetResult().Document,
                volumeProperty,
                37.5,
                "streaming loader");
            AssertVolume(
                AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document,
                volumeProperty,
                37.5,
                "flat streaming loader");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySettingsEditSaveAndUndo()
    {
        SettingsContract contract = SettingsContract.Discover();
        string sourcePath = WriteFixture("RoundTrip", volume: 72.0, unknownValue: "keep-me");
        string savedPath = Path.ChangeExtension(sourcePath, ".saved.adofai");
        try
        {
            LevelDocument document = AdoFaiLoader.Load(sourcePath).Document;
            var session = new EditorSession(document);
            object before = contract.Get(session);
            object after = contract.Create(
                songFilename: "replacement.ogg",
                initialBpm: 180.0,
                songVolumePercent: 64.0,
                offsetMilliseconds: -125.5,
                pitchPercent: 110.0,
                defaultHitSound: "Hat",
                hitSoundVolumePercent: 45.0);

            contract.Edit(session, after);
            AssertDocumentSettings(document, contract, after, "edit");
            if (!session.CanUndo)
                throw new InvalidOperationException("Song Settings edit was not recorded as one undo operation.");

            session.Undo();
            AssertDocumentSettings(document, contract, before, "undo");
            session.Redo();
            AssertDocumentSettings(document, contract, after, "redo");

            session.SaveAsync(savedPath).GetAwaiter().GetResult();
            JsonObject root = LooseAdoFaiJson.ParseObject(savedPath);
            JsonObject settings = root["settings"] as JsonObject
                ?? throw new InvalidOperationException("Saved source lost the settings object.");

            AssertJsonString(settings, "songFilename", "replacement.ogg");
            AssertJsonNumber(settings, "bpm", 180.0);
            AssertJsonNumber(settings, "volume", 64.0);
            AssertJsonNumber(settings, "offset", -125.5);
            AssertJsonNumber(settings, "pitch", 110.0);
            AssertJsonString(settings, "hitsound", "Hat");
            AssertJsonNumber(settings, "hitsoundVolume", 45.0);
            if (settings["futureSongSetting"]?["token"]?.GetValue<string>() != "keep-me")
                throw new InvalidOperationException("Saving Song Settings discarded an unknown settings property.");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(savedPath);
        }
    }

    private static void VerifyInitialBpmReevaluatesSetSpeed()
    {
        SettingsContract contract = SettingsContract.Discover();
        string path = WriteFixture(
            "SetSpeed",
            volume: 100.0,
            unknownValue: "speed",
            actions: """
            [
              { "floor": 1, "eventType": "SetSpeed", "active": true, "speedType": "Multiplier", "bpmMultiplier": 2 },
              { "floor": 2, "eventType": "SetSpeed", "active": true, "speedType": "Bpm", "beatsPerMinute": 300 }
            ]
            """);
        try
        {
            LevelDocument document = AdoFaiLoader.Load(path).Document;
            var session = new EditorSession(document);
            contract.Edit(session, contract.Create(
                document.SongFilename,
                200.0,
                contract.ReadVolume(document),
                document.OffsetMilliseconds,
                document.PitchPercent,
                document.DefaultHitSound,
                document.HitSoundVolumePercent));

            LevelAction absolute = document.ActionStore.Actions.Single(action =>
                action.Floor == 2 && action.Kind == LevelActionKind.SetSpeed);
            AssertNear(0.75, absolute.SpeedRatio ?? double.NaN,
                "SetSpeed SpeedRatio after InitialBpm change");

            LevelAction multiplier = document.ActionStore.Actions.Single(action => action.Floor == 1);
            if (!SetSpeedEditorConversion.TryGetPreviousEffectiveBpm(document, multiplier, out double previous))
                throw new InvalidOperationException("SetSpeed conversion could not resolve its previous BPM.");
            AssertNear(200.0, previous, "SetSpeed editor previous BPM after InitialBpm change");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySongVolumeContract()
    {
        PropertyInfo? property = typeof(AudioPlayer).GetProperty(
            "SongVolumePercent",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null || !property.CanRead || !property.CanWrite)
        {
            throw new InvalidOperationException(
                "AudioPlayer has no song-branch-only volume control; settings.volume cannot be isolated from hitsounds.");
        }

        var player = new AudioPlayer();
        try
        {
            player.HitSoundsEnabled = true;
            property.SetValue(player, 35.0);
            AssertNear(35.0, Convert.ToDouble(property.GetValue(player)), "song volume property");
            if (!player.HitSoundsEnabled)
                throw new InvalidOperationException("Changing song volume altered the hitsound branch.");
        }
        finally
        {
            player.Dispose();
        }
    }

    private static void VerifyOffsetAndPitchUpdate()
    {
        SettingsContract contract = SettingsContract.Discover();
        LevelDocument document = LevelDocument.CreateSynthetic(3);
        document.SeparateCountdownTime = false;
        var session = new EditorSession(document);
        contract.Edit(session, contract.Create(null, 120.0, 100.0, 250.0, 200.0, "Kick", 100.0));

        AssertNear(250.0, document.OffsetMilliseconds, "offset edit");
        AssertNear(200.0, document.PitchPercent, "pitch edit");
        AssertNear(-0.25, PlaybackClock.AudioToChartTime(document, 0.0), "offset/pitch chart anchor");
        AssertNear(0.625, PlaybackClock.ChartToAudioTime(document, 1.0), "offset/pitch audio mapping");
    }

    private static void VerifySongFilenameRelativePath()
    {
        SettingsContract contract = SettingsContract.Discover();
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-SongSettings-Path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "chart.adofai");
            File.WriteAllText(source, "{}");
            LevelDocument document = LevelDocument.CreateSynthetic(2);
            document.SourcePath = source;
            var session = new EditorSession(document);
            contract.Edit(session, contract.Create("audio/song.ogg", 100, 100, 0, 100, "Kick", 100));

            string expected = Path.GetFullPath(Path.Combine(directory, "audio", "song.ogg"));
            if (!string.Equals(document.ResolveSongPath(), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Edited songFilename was not resolved relative to the chart.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyDefaultHitSoundUpdate()
    {
        SettingsContract contract = SettingsContract.Discover();
        LevelDocument document = LevelDocument.CreateSynthetic(3);
        var session = new EditorSession(document);
        contract.Edit(session, contract.Create(null, 100, 100, 0, 100, "Hat", 35));

        HitSoundState initial = HitSoundTimelineBuilder.Build(document).InitialState;
        if (!string.Equals(initial.Name, "Hat", StringComparison.Ordinal) ||
            Math.Abs(initial.Volume - 0.35) > 1.0e-9)
        {
            throw new InvalidOperationException(
                $"Default hitsound edit did not refresh timeline input: name={initial.Name}, volume={initial.Volume}.");
        }
    }

    private static void VerifySidebarShell()
    {
        string path = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "ExtremeEditor.Wpf",
            "MainWindow.xaml");
        XDocument xaml = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        XElement root = xaml.Root ?? throw new InvalidOperationException("MainWindow.xaml has no root element.");

        XElement sidebar = RequireNamedElement(root, "SettingsSidebarHost");
        XElement rail = RequireNamedElement(root, "SettingsCategoryRail");
        _ = RequireNamedElement(root, "SongSettingsPane");
        _ = RequireNamedElement(root, "SettingsSidebarCollapseButton");
        _ = RequireNamedElement(root, "SongSettingsCategoryButton");
        XElement inspector = RequireNamedElement(root, "EventInspectorHost");

        foreach (string control in new[]
                 {
                     "SongFilenameTextBox", "SongBpmTextBox", "SongVolumeTextBox",
                     "SongOffsetTextBox", "SongPitchTextBox", "SongHitSoundComboBox",
                     "SongHitSoundVolumeTextBox"
                 })
        {
            _ = RequireNamedElement(root, control);
        }

        if (inspector.AncestorsAndSelf().Contains(sidebar))
            throw new InvalidOperationException("Song Settings sidebar must be independent from Event Inspector.");

        double railWidth = ReadRequiredWidth(rail, "category rail");
        if (railWidth is < 36 or > 44)
            throw new InvalidOperationException($"Settings category rail width must be 36-44px; actual={railWidth}.");

        XElement contentColumn = RequireNamedElement(root, "SettingsContentColumn");
        double contentWidth = ReadRequiredWidth(contentColumn, "settings content pane");
        if (contentWidth is < 260 or > 300)
            throw new InvalidOperationException(
                $"Song Settings pane width must be 260-300px; actual={contentWidth}.");
        if (!double.TryParse(contentColumn.Attribute("MaxWidth")?.Value, out double maxWidth) || maxWidth > 300)
            throw new InvalidOperationException("Settings content pane needs a 300px maximum for narrow windows.");

        XElement viewportColumn = RequireNamedElement(root, "EditorViewportColumn");
        if (!double.TryParse(viewportColumn.Attribute("MinWidth")?.Value, out double minViewport) || minViewport < 350)
            throw new InvalidOperationException("The viewport column must retain usable width beside the sidebar.");
    }

    private static XElement RequireNamedElement(XElement root, string name) =>
        root.DescendantsAndSelf().FirstOrDefault(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                string.Equals(attribute.Value, name, StringComparison.Ordinal)))
        ?? throw new InvalidOperationException($"Song Settings XAML element '{name}' is missing.");

    private static double ReadRequiredWidth(XElement element, string label)
    {
        if (!double.TryParse(element.Attribute("Width")?.Value, out double width))
            throw new InvalidOperationException($"The {label} needs a fixed numeric Width.");
        return width;
    }

    private static void AssertDocumentSettings(
        LevelDocument document,
        SettingsContract contract,
        object snapshot,
        string phase)
    {
        if (!string.Equals(document.SongFilename, contract.ReadString(snapshot, "SongFilename"), StringComparison.Ordinal) ||
            !string.Equals(document.DefaultHitSound, contract.ReadString(snapshot, "DefaultHitSound"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Song Settings {phase} did not update string settings.");
        }
        AssertNear(contract.ReadDouble(snapshot, "InitialBpm"), document.InitialBpm, $"{phase} bpm");
        AssertNear(contract.ReadDouble(snapshot, "SongVolumePercent"), contract.ReadVolume(document), $"{phase} volume");
        AssertNear(contract.ReadDouble(snapshot, "OffsetMilliseconds"), document.OffsetMilliseconds, $"{phase} offset");
        AssertNear(contract.ReadDouble(snapshot, "PitchPercent"), document.PitchPercent, $"{phase} pitch");
        AssertNear(contract.ReadDouble(snapshot, "HitSoundVolumePercent"), document.HitSoundVolumePercent, $"{phase} hitsound volume");
    }

    private static string WriteFixture(
        string name,
        double volume,
        string unknownValue,
        string actions = "[]")
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-SongSettings-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, $$"""
        {
          "angleData": [0, 90, 180],
          "settings": {
            "songFilename": "original.ogg",
            "bpm": 100,
            "volume": {{volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "offset": 20,
            "pitch": 100,
            "hitsound": "Kick",
            "hitsoundVolume": 100,
            "futureSongSetting": { "token": "{{unknownValue}}" }
          },
          "actions": {{actions}},
          "decorations": []
        }
        """);
        return path;
    }

    private static void AssertVolume(LevelDocument document, PropertyInfo property, double expected, string label) =>
        AssertNear(expected, Convert.ToDouble(property.GetValue(document)), label);

    private static void AssertJsonString(JsonObject settings, string key, string expected)
    {
        string? actual = settings[key]?.GetValue<string>();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Saved settings.{key}: expected={expected}, actual={actual ?? "<null>"}.");
    }

    private static void AssertJsonNumber(JsonObject settings, string key, double expected)
    {
        double actual = settings[key]?.GetValue<double>() ?? double.NaN;
        AssertNear(expected, actual, $"saved settings.{key}");
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1.0e-8)
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

    private sealed class SettingsContract
    {
        private readonly Type _snapshotType;
        private readonly ConstructorInfo _constructor;
        private readonly MethodInfo _get;
        private readonly MethodInfo _edit;
        private readonly PropertyInfo _volumeProperty;

        private SettingsContract(
            Type snapshotType,
            ConstructorInfo constructor,
            MethodInfo get,
            MethodInfo edit,
            PropertyInfo volumeProperty)
        {
            _snapshotType = snapshotType;
            _constructor = constructor;
            _get = get;
            _edit = edit;
            _volumeProperty = volumeProperty;
        }

        internal static SettingsContract Discover()
        {
            Assembly assembly = typeof(EditorSession).Assembly;
            Type snapshotType = assembly.GetType(SnapshotTypeName)
                ?? throw new InvalidOperationException("typed LevelSettingsSnapshot is missing.");
            ConstructorInfo constructor = snapshotType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(double), typeof(double), typeof(double), typeof(double), typeof(string), typeof(double)],
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "LevelSettingsSnapshot must retain all seven Song Settings values.");
            MethodInfo get = typeof(EditorSession).GetMethod(
                "GetLevelSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.GetLevelSettings() is missing.");
            MethodInfo edit = typeof(EditorSession).GetMethod(
                "EditLevelSettings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [snapshotType],
                modifiers: null)
                ?? throw new InvalidOperationException("EditorSession.EditLevelSettings(snapshot) is missing.");
            PropertyInfo volume = typeof(LevelDocument).GetProperty("SongVolumePercent")
                ?? throw new InvalidOperationException("LevelDocument.SongVolumePercent is missing.");
            return new SettingsContract(snapshotType, constructor, get, edit, volume);
        }

        internal object Create(
            string? songFilename,
            double initialBpm,
            double songVolumePercent,
            double offsetMilliseconds,
            double pitchPercent,
            string defaultHitSound,
            double hitSoundVolumePercent) =>
            _constructor.Invoke(
            [
                songFilename, initialBpm, songVolumePercent, offsetMilliseconds,
                pitchPercent, defaultHitSound, hitSoundVolumePercent
            ]);

        internal object Get(EditorSession session) =>
            _get.Invoke(session, null)
            ?? throw new InvalidOperationException("EditorSession.GetLevelSettings returned null.");

        internal void Edit(EditorSession session, object snapshot) => _edit.Invoke(session, [snapshot]);

        internal double ReadVolume(LevelDocument document) => Convert.ToDouble(_volumeProperty.GetValue(document));

        internal double ReadDouble(object snapshot, string property) =>
            Convert.ToDouble(RequireSnapshotProperty(property).GetValue(snapshot));

        internal string? ReadString(object snapshot, string property) =>
            RequireSnapshotProperty(property).GetValue(snapshot) as string;

        private PropertyInfo RequireSnapshotProperty(string name) =>
            _snapshotType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"LevelSettingsSnapshot.{name} is missing.");
    }
}
