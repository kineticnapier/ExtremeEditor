using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TrackAnimationSettingsRegression
{
    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "typed root animation state", VerifyTypedState);
        Check(failures, "root defaults and explicit values", VerifyRootRuntime);
        Check(failures, "edit undo redo and save preservation", VerifyEditUndoSave);
        Check(failures, "AnimateTrack persistent override", VerifyActionOverride);
        Check(failures, "save ownership and UI contract", VerifyOwnershipAndUi);
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "root Track Animation Settings are not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyEditUndoSave()
    {
        string path = WriteFixture("Edit", new JsonObject
        {
            ["trackAnimation"] = "Grow",
            ["beatsAhead"] = 3.0,
            ["trackDisappearAnimation"] = "Fade",
            ["beatsBehind"] = 4.0,
            ["stickToFloors"] = false,
            ["floorIconOutlines"] = true
        }, new JsonArray(new JsonObject
        {
            ["floor"] = 2, ["eventType"] = "AnimateTrack", ["active"] = true,
            ["trackAnimation"] = "Rise", ["futureActionProperty"] = "keep"
        }));
        string saved = Path.ChangeExtension(path, ".saved.adofai");
        try
        {
            LevelDocument document = WpfLevelLoader.Load(path).Document;
            var session = new EditorSession(document);
            TrackSettingsSnapshot before = session.GetTrackSettings();
            session.EditTrackSettings(before);
            if (session.IsDirty)
                throw new InvalidOperationException("no-op edit changed dirty/history state.");

            TrackSettingsSnapshot after = before with
            {
                TrackAnimation = "Drop",
                BeatsAhead = 1.5,
                TrackDisappearAnimation = "Retract",
                BeatsBehind = 2.5
            };
            session.EditTrackSettings(after);
            if (!session.IsDirty || session.GetTrackSettings() != after)
                throw new InvalidOperationException("typed edit did not update the snapshot/dirty state.");
            AssertAnimationCache(document, after, "edit");
            AssertAnimationSource(session, after, "edit");

            session.Undo();
            if (session.GetTrackSettings() != before)
                throw new InvalidOperationException("undo did not restore root animation settings.");
            AssertAnimationCache(document, before, "undo");
            AssertAnimationSource(session, before, "undo");
            session.Redo();
            if (session.GetTrackSettings() != after)
                throw new InvalidOperationException("redo did not restore edited root animation settings.");

            JsonObject original = LooseAdoFaiJson.ParseObject(path);
            session.SaveAsync(saved).GetAwaiter().GetResult();
            JsonObject output = LooseAdoFaiJson.ParseObject(saved);
            foreach (string key in new[]
                     {
                         "trackTexture", "futureSetting", "trackColor", "stickToFloors", "floorIconOutlines"
                     })
            {
                if (!JsonNode.DeepEquals(original["settings"]?[key], output["settings"]?[key]))
                    throw new InvalidOperationException($"editing animation settings changed preserved {key}.");
            }
            if (!JsonNode.DeepEquals(original["actions"], output["actions"]) ||
                !JsonNode.DeepEquals(original["futureRoot"], output["futureRoot"]))
            {
                throw new InvalidOperationException("editing root settings changed actions or unknown root data.");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(saved);
        }
    }

    private static void AssertAnimationCache(LevelDocument document, TrackSettingsSnapshot expected, string phase)
    {
        if (!TrackAnimationMetadataCache.TryGet(document, out TrackAnimationSettingsData actual) ||
            actual.TrackAnimation != expected.TrackAnimation ||
            actual.BeatsAhead != expected.BeatsAhead ||
            actual.TrackDisappearAnimation != expected.TrackDisappearAnimation ||
            actual.BeatsBehind != expected.BeatsBehind)
        {
            throw new InvalidOperationException($"{phase} metadata cache mismatch.");
        }
    }

    private static void AssertAnimationSource(EditorSession session, TrackSettingsSnapshot expected, string phase)
    {
        JsonObject settings = session.GetSourceRootForSave()["settings"]!.AsObject();
        if (settings["trackAnimation"]?.GetValue<string>() != expected.TrackAnimation ||
            settings["beatsAhead"]?.GetValue<double>() != expected.BeatsAhead ||
            settings["trackDisappearAnimation"]?.GetValue<string>() != expected.TrackDisappearAnimation ||
            settings["beatsBehind"]?.GetValue<double>() != expected.BeatsBehind)
        {
            throw new InvalidOperationException($"{phase} source root mismatch.");
        }
    }

    private static void VerifyTypedState()
    {
        Type snapshot = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.TrackSettingsSnapshot")
            ?? throw new InvalidOperationException("TrackSettingsSnapshot is missing.");
        foreach ((string name, Type type) in new[]
                 {
                     ("TrackAnimation", typeof(string)), ("BeatsAhead", typeof(double)),
                     ("TrackDisappearAnimation", typeof(string)), ("BeatsBehind", typeof(double))
                 })
        {
            PropertyInfo property = snapshot.GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"TrackSettingsSnapshot.{name} is missing.");
            if (property.PropertyType != type)
                throw new InvalidOperationException($"TrackSettingsSnapshot.{name} has the wrong type.");
        }
        if (snapshot.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .All(static constructor => constructor.GetParameters().Length != 12))
        {
            throw new InvalidOperationException("TrackSettingsSnapshot does not accept the four root animation values.");
        }
    }

    private static void VerifyRootRuntime()
    {
        string defaults = WriteFixture("Defaults", new JsonObject(), new JsonArray());
        string explicitValues = WriteFixture("Explicit", new JsonObject
        {
            ["trackAnimation"] = "Grow_Spin",
            ["beatsAhead"] = 2.5,
            ["trackDisappearAnimation"] = "Shrink_Spin",
            ["beatsBehind"] = 6.5
        }, new JsonArray());
        try
        {
            NativeTrackAnimationSegment initial = Build(defaults).Single();
            AssertSegment(initial, 0, 3, NativeTrackAnimationSegment.AppearNone, 3,
                NativeTrackAnimationSegment.DisappearNone, 4, "defaults");

            NativeTrackAnimationSegment configured = Build(explicitValues).Single();
            AssertSegment(configured, 0, 3, NativeTrackAnimationSegment.AppearGrowSpin, 2.5,
                NativeTrackAnimationSegment.DisappearShrinkSpin, 6.5, "explicit root");
        }
        finally
        {
            File.Delete(defaults);
            File.Delete(explicitValues);
        }
    }

    private static void VerifyActionOverride()
    {
        string path = WriteFixture("Override", new JsonObject
        {
            ["trackAnimation"] = "Grow",
            ["beatsAhead"] = 3.0,
            ["trackDisappearAnimation"] = "Fade",
            ["beatsBehind"] = 4.0
        }, new JsonArray(new JsonObject
        {
            ["floor"] = 2,
            ["eventType"] = "AnimateTrack",
            ["active"] = true,
            ["trackAnimation"] = "Rise",
            ["beatsAhead"] = 1.25,
            ["trackDisappearAnimation"] = "Retract",
            ["beatsBehind"] = 2.25,
            ["futureActionProperty"] = "keep"
        }));
        try
        {
            NativeTrackAnimationSegment[] segments = Build(path);
            if (segments.Length != 2)
                throw new InvalidOperationException($"expected root + action segments, got {segments.Length}.");
            AssertSegment(segments[0], 0, 1, NativeTrackAnimationSegment.AppearGrow, 3,
                NativeTrackAnimationSegment.DisappearFade, 4, "root segment");
            AssertSegment(segments[1], 2, 3, NativeTrackAnimationSegment.AppearRise, 1.25,
                NativeTrackAnimationSegment.DisappearRetract, 2.25, "action segment");

            NativeTrackAnimationSegment[] rebuilt = Build(path);
            if (!segments.SequenceEqual(rebuilt))
                throw new InvalidOperationException("seek/rebuild input was not deterministic.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyOwnershipAndUi()
    {
        string xamlPath = Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow.xaml");
        XDocument xaml = XDocument.Load(xamlPath);
        string[] names =
        [
            "TrackAppearAnimationComboBox", "TrackBeatsAheadTextBox",
            "TrackDisappearAnimationComboBox", "TrackBeatsBehindTextBox"
        ];
        foreach (string name in names)
        {
            if (!xaml.Descendants().Any(element =>
                    string.Equals(element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value,
                        name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Track Settings UI element {name} is missing.");
            }
        }
        string source = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "Editor", "EditorSession.TrackSettings.cs"));
        foreach (string key in new[] { "trackAnimation", "beatsAhead", "trackDisappearAnimation", "beatsBehind" })
        {
            if (!source.Contains($"sourceSettings[\"{key}\"]", StringComparison.Ordinal))
                throw new InvalidOperationException($"save ownership for settings.{key} is missing.");
        }
        if (source.Contains("actions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("root Track Settings edit rewrites AnimateTrack actions.");
    }

    private static NativeTrackAnimationSegment[] Build(string path)
    {
        LevelDocument document = WpfLevelLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(document);
        return TrackAnimationTimelineBuilder.Build(document, timing).Segments;
    }

    private static void AssertSegment(
        NativeTrackAnimationSegment actual,
        int start,
        int end,
        uint appear,
        double ahead,
        uint disappear,
        double behind,
        string label)
    {
        if (actual.StartFloor != start || actual.EndFloor != end ||
            actual.AppearType != appear || actual.DisappearType != disappear ||
            Math.Abs(actual.BeatsAhead - ahead) > 0.000001 ||
            Math.Abs(actual.BeatsBehind - behind) > 0.000001)
        {
            throw new InvalidOperationException(
                $"{label} mismatch: floors={actual.StartFloor}..{actual.EndFloor}, " +
                $"appear={actual.AppearType}/{actual.BeatsAhead}, " +
                $"disappear={actual.DisappearType}/{actual.BeatsBehind}.");
        }
    }

    private static string WriteFixture(string name, JsonObject overrides, JsonArray actions)
    {
        var settings = new JsonObject
        {
            ["songFilename"] = "song.ogg", ["bpm"] = 120, ["pitch"] = 100,
            ["trackColor"] = "debb7b", ["secondaryTrackColor"] = "ffffff",
            ["trackColorType"] = "Single", ["trackColorAnimDuration"] = 2,
            ["trackColorPulse"] = "None", ["trackPulseLength"] = 10,
            ["trackStyle"] = "Standard", ["trackGlowIntensity"] = 100,
            ["trackTexture"] = "keep.png", ["futureSetting"] = "keep"
        };
        foreach ((string key, JsonNode? value) in overrides)
            settings[key] = value?.DeepClone();
        var root = new JsonObject
        {
            ["angleData"] = new JsonArray(0, 90, 180),
            ["settings"] = settings,
            ["actions"] = actions,
            ["decorations"] = new JsonArray(),
            ["futureRoot"] = "keep"
        };
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-TrackAnimationSettings-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static void Check(List<string> failures, string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }
}
