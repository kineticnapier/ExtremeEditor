using System.Collections;
using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// Flash, Bloom, SetFilter and ScreenTile read their parameters from the
// action's JSON. A freshly loaded chart must render the values in the file,
// exactly as if each event had been edited to the same JSON, and an edit
// must still take precedence.
internal static class VfxSourcePropertiesRegression
{
    private static readonly JsonObject[] SourceActions =
    [
        Parse("""{"floor": 1, "eventType": "Flash", "duration": 4, "plane": "Foreground", "startColor": "ff0000", "startOpacity": 80, "endColor": "00ff00", "endOpacity": 30, "angleOffset": 0, "ease": "OutQuad"}"""),
        Parse("""{"floor": 2, "eventType": "Bloom", "enabled": true, "threshold": 25, "intensity": 150, "color": "abcdef", "duration": 2, "angleOffset": 0, "ease": "InSine"}"""),
        Parse("""{"floor": 3, "eventType": "SetFilter", "filter": "Invert", "enabled": true, "intensity": 70, "duration": 1, "disableOthers": true, "angleOffset": 0, "ease": "OutCubic"}"""),
        Parse("""{"floor": 4, "eventType": "ScreenTile", "duration": 3, "tile": [3, 2], "angleOffset": 0, "ease": "InOutSine"}""")
    ];

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "vfx-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "vfx.adofai");
            var root = new JsonObject
            {
                ["angleData"] = new JsonArray(Enumerable.Repeat(0, 12).Select(static a => (JsonNode)a).ToArray()),
                ["settings"] = new JsonObject { ["version"] = 15, ["bpm"] = 100, ["offset"] = 0, ["pitch"] = 100 },
                ["actions"] = new JsonArray(SourceActions.Select(static a => (JsonNode)a.DeepClone()).ToArray()),
                ["decorations"] = new JsonArray()
            };
            File.WriteAllText(path, root.ToJsonString());

            VerifyLoadedValuesReachTimelines(path);
            VerifyEditOverridesLoadedValues(path);
            VerifyBloomAndFilterNumericSafety();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyLoadedValuesReachTimelines(string path)
    {
        LevelDocument loaded = WpfLevelLoader.Load(path).Document;
        TimingMap timing = FlatTimingMapBuilder.Build(loaded);
        Timelines actual = Build(loaded, timing);

        // Oracle: the same events edited to exactly the JSON in the file.
        LevelDocument edited = WpfLevelLoader.Load(path).Document;
        LevelAction[] actions = edited.ActionStore.Actions.ToArray();
        for (int i = 0; i < actions.Length; i++)
            actions[i].PropertyOverrides = (JsonObject)SourceActions[i].DeepClone();
        Timelines expected = Build(edited, timing);

        AssertSame("Flash", actual.Flash, expected.Flash);
        AssertSame("Bloom", actual.Bloom, expected.Bloom);
        AssertSame("SetFilter", actual.Filter, expected.Filter);
        AssertSame("ScreenTile", actual.ScreenTile, expected.ScreenTile);

        // Guard the oracle itself: these are not the builder defaults.
        NativeFlashEvent flash = actual.Flash.Single();
        if (flash.StartColor != 0xff0000u || flash.Plane != NativeFlashEvent.PlaneForeground)
            throw new InvalidOperationException("Loaded Flash did not keep its file colour and plane.");
        if (actual.ScreenTile.Length != 1)
            throw new InvalidOperationException("Loaded ScreenTile did not produce one event.");
    }

    private static void VerifyEditOverridesLoadedValues(string path)
    {
        LevelDocument level = WpfLevelLoader.Load(path).Document;
        TimingMap timing = FlatTimingMapBuilder.Build(level);
        LevelAction flash = level.ActionStore.Actions.Single(a => a.EventType == "Flash");
        JsonObject overrides = (JsonObject)SourceActions[0].DeepClone();
        overrides["startColor"] = "0000ff";
        overrides["plane"] = "Background";
        flash.PropertyOverrides = overrides;

        NativeFlashEvent result = FlashTimelineBuilder.Build(level, timing).Single();
        if (result.StartColor != 0x0000ffu || result.Plane != NativeFlashEvent.PlaneBackground)
            throw new InvalidOperationException("An edited Flash did not take precedence over the loaded values.");
    }

    private static void VerifyBloomAndFilterNumericSafety()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(12);
        level.InitialBpm = 100;
        level.ReplaceActions(
        [
            Vfx(1, 0, "Bloom", new JsonObject
            {
                ["enabled"] = true, ["threshold"] = "NaN", ["intensity"] = "Infinity",
                ["color"] = "ffffff", ["duration"] = 0, ["ease"] = "Linear"
            }),
            Vfx(2, 1, "Bloom", new JsonObject
            {
                ["enabled"] = true, ["threshold"] = 25d, ["intensity"] = -100d,
                ["color"] = "ffffff", ["duration"] = 0, ["ease"] = "Linear"
            }),
            Vfx(3, 2, "Bloom", new JsonObject
            {
                ["enabled"] = true, ["threshold"] = 25d, ["intensity"] = 500d,
                ["color"] = "ffffff", ["duration"] = 0, ["ease"] = "Linear"
            }),
            Vfx(4, 3, "SetFilter", new JsonObject
            {
                ["filter"] = "Sepia", ["enabled"] = true, ["intensity"] = 1e41,
                ["duration"] = 0, ["ease"] = "Linear", ["disableOthers"] = false
            }),
            Vfx(5, 4, "SetFilter", new JsonObject
            {
                ["filter"] = "Contrast", ["enabled"] = true, ["intensity"] = "-Infinity",
                ["duration"] = 0, ["ease"] = "Linear", ["disableOthers"] = false
            })
        ]);

        TimingMap timing = FlatTimingMapBuilder.Build(level);
        NativeBloomEvent[] bloom = BloomTimelineBuilder.Build(level, timing);
        NativeFilterEvent[] filter = FilterTimelineBuilder.Build(level, timing);
        if (bloom.Length != 3 || bloom[0].Threshold != 0f || bloom[0].Intensity != 0f ||
            bloom[1].Intensity != -1f || bloom[2].Intensity != 5f)
            throw new InvalidOperationException(
                "Bloom did not reject non-finite values or preserve finite negative/500% intensity: " +
                string.Join("; ", bloom.Select(static item =>
                    $"threshold={item.Threshold:R}, intensity={item.Intensity:R}")));
        if (filter.Length != 2 || filter[0].TargetIntensity != 1f || filter[1].TargetIntensity != 1f)
            throw new InvalidOperationException("SetFilter did not reject float overflow and non-finite text values.");

        NativeFilterState recovered = FilterStateEvaluator.Evaluate(
        [
            new NativeFilterEvent { StartTime = 0, Filter = 0, Enabled = 1, TargetIntensity = float.NaN },
            new NativeFilterEvent { StartTime = 1, Filter = 0, Enabled = 1, TargetIntensity = 0.75f }
        ], 2)[0];
        if (!recovered.Enabled || recovered.Intensity != 0.75f)
            throw new InvalidOperationException("A malformed SetFilter event poisoned the following valid event.");

        NativeFilterState overshot = FilterStateEvaluator.Evaluate(
        [
            new NativeFilterEvent { StartTime = 0, Filter = 1, Enabled = 1, TargetIntensity = 0f },
            new NativeFilterEvent
            {
                StartTime = 1, DurationSeconds = 4, Filter = 1, Enabled = 1,
                TargetIntensity = 1f, Ease = NativeCameraEvent.EaseOutBack
            }
        ], 4)[1];
        if (!float.IsFinite(overshot.Intensity) || overshot.Intensity <= 1f)
            throw new InvalidOperationException("SetFilter did not preserve finite ease overshoot.");

        NativeBloomState bloomOvershot = BloomStateEvaluator.Evaluate(
        [
            new NativeBloomEvent { StartTime = 0, Enabled = 1, Threshold = 0f, Intensity = 0f },
            new NativeBloomEvent
            {
                StartTime = 1, DurationSeconds = 4, Enabled = 1,
                Threshold = 1f, Intensity = float.MaxValue, Ease = NativeCameraEvent.EaseOutBack
            }
        ], 4);
        if (!float.IsFinite(bloomOvershot.Intensity) || bloomOvershot.Threshold <= 1f)
            throw new InvalidOperationException("Bloom did not preserve finite ease overshoot.");
    }

    private static LevelAction Vfx(int floor, int sourceIndex, string eventType, JsonObject properties)
    {
        properties["floor"] = floor;
        properties["eventType"] = eventType;
        var action = new LevelAction(floor, eventType, true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            Duration = 0,
            PropertyOverrides = properties
        };
        return action;
    }

    private readonly record struct Timelines(
        NativeFlashEvent[] Flash,
        NativeBloomEvent[] Bloom,
        NativeFilterEvent[] Filter,
        NativeScreenTileEvent[] ScreenTile);

    private static Timelines Build(LevelDocument level, TimingMap timing) => new(
        FlashTimelineBuilder.Build(level, timing),
        BloomTimelineBuilder.Build(level, timing),
        FilterTimelineBuilder.Build(level, timing),
        ScreenTileTimelineBuilder.Build(level, timing));

    private static void AssertSame(string name, Array actual, Array expected)
    {
        if (!StructuralComparisons.StructuralEqualityComparer.Equals(actual, expected))
        {
            string Describe(Array items) => string.Join("; ", items.Cast<object>());
            throw new InvalidOperationException(
                $"Loaded {name} differs from the same values applied as an edit: " +
                $"actual=[{Describe(actual)}] expected=[{Describe(expected)}].");
        }
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
