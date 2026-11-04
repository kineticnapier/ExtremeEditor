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
            VerifyNonFiniteVisualParametersFallback();
            VerifyFiniteInterpolationWithOpposingExtremes();
            VerifyFiniteFilterEffectScaling();
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

    private static void VerifyNonFiniteVisualParametersFallback()
    {
        // Malformed or overflowing visual values must not cross the native ABI
        // as NaN/Infinity. Existing defaults remain authoritative.
        LevelDocument level = LevelDocument.CreateSynthetic(6);
        level.ReplaceActions(
        [
            new LevelAction(1, "Flash", true, null, null, null, null)
            {
                SourceIndex = 0,
                PropertyOverrides = new JsonObject
                {
                    ["startOpacity"] = "NaN",
                    ["endOpacity"] = "Infinity"
                }
            },
            new LevelAction(2, "Bloom", true, null, null, null, null)
            {
                SourceIndex = 1,
                PropertyOverrides = new JsonObject
                {
                    ["threshold"] = "NaN",
                    ["intensity"] = "Infinity"
                }
            },
            new LevelAction(3, "SetFilter", true, null, null, null, null)
            {
                SourceIndex = 2,
                PropertyOverrides = new JsonObject
                {
                    ["filter"] = "Invert",
                    ["intensity"] = "1e100"
                }
            },
            new LevelAction(4, "ScreenTile", true, null, null, null, null)
            {
                SourceIndex = 3,
                PropertyOverrides = new JsonObject
                {
                    ["tile"] = new JsonArray { double.MaxValue, "NaN" }
                }
            }
        ]);

        TimingMap timing = FlatTimingMapBuilder.Build(level);
        NativeFlashEvent flash = FlashTimelineBuilder.Build(level, timing).Single();
        NativeBloomEvent bloom = BloomTimelineBuilder.Build(level, timing).Single();
        NativeFilterEvent filter = FilterTimelineBuilder.Build(level, timing).Single();
        NativeScreenTileEvent tile = ScreenTileTimelineBuilder.Build(level, timing).Single();

        if (flash.StartOpacity != 1f || flash.EndOpacity != 0f ||
            bloom.Threshold != 0f || bloom.Intensity != 0f ||
            filter.TargetIntensity != 1f ||
            tile.TargetX != 1f || tile.TargetY != 1f)
        {
            throw new InvalidOperationException(
                "Non-finite or float-overflowing VFX parameters must use finite defaults.");
        }
    }

    private static void VerifyFiniteInterpolationWithOpposingExtremes()
    {
        // Two finite float endpoints can overflow when subtracted as floats.
        // At the halfway point the expected result is zero, not Infinity.
        const float large = 2e38f;
        NativeBloomState bloom = BloomStateEvaluator.Evaluate(
            new[]
            {
                new NativeBloomEvent
                {
                    StartTime = 0, DurationSeconds = 0,
                    Threshold = large, Intensity = -large, Enabled = 1
                },
                new NativeBloomEvent
                {
                    StartTime = 1, DurationSeconds = 2,
                    Threshold = -large, Intensity = large, Enabled = 1
                }
            }, 2.0);
        if (!float.IsFinite(bloom.Threshold) || !float.IsFinite(bloom.Intensity) ||
            Math.Abs(bloom.Threshold) > 1e30f || Math.Abs(bloom.Intensity) > 1e30f)
            throw new InvalidOperationException("Bloom interpolation overflowed finite endpoints.");

        NativeFilterState filter = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 0,
                    Filter = 0, TargetIntensity = -large, Enabled = 1
                },
                new NativeFilterEvent
                {
                    StartTime = 1, DurationSeconds = 2,
                    Filter = 0, TargetIntensity = large, Enabled = 1
                }
            }, 2.0)[0];
        if (!float.IsFinite(filter.Intensity) || !float.IsFinite(filter.AppliedValue) ||
            Math.Abs(filter.Intensity) > 1e30f)
            throw new InvalidOperationException("SetFilter interpolation overflowed finite endpoints.");

        NativeScreenTileState tile = ScreenTileStateEvaluator.Evaluate(
            new[]
            {
                new NativeScreenTileEvent
                {
                    StartTime = 0, DurationSeconds = 0,
                    TargetX = large, TargetY = -large
                },
                new NativeScreenTileEvent
                {
                    StartTime = 1, DurationSeconds = 2,
                    TargetX = -large, TargetY = large,
                    Flags = NativeScreenTileEvent.FlagEnableBeforeTween
                }
            }, 2.0);
        if (!float.IsFinite(tile.TileX) || !float.IsFinite(tile.TileY) ||
            Math.Abs(tile.TileX) > 1e30f || Math.Abs(tile.TileY) > 1e30f)
            throw new InvalidOperationException("ScreenTile interpolation overflowed finite endpoints.");
    }

    private static void VerifyFiniteFilterEffectScaling()
    {
        // An effect-specific multiplier must not overflow an otherwise finite
        // intensity before the value reaches the native renderer.
        foreach (float extreme in new[] { float.MaxValue, -float.MaxValue })
        {
            for (uint filter = 0; filter < 41; filter++)
            {
                float applied = FilterStateEvaluator.ApplyFilterIntensity(filter, extreme);
                if (!float.IsFinite(applied))
                    throw new InvalidOperationException(
                        $"SetFilter {filter} produced a non-finite value from {extreme}.");
            }
        }

        if (FilterStateEvaluator.ApplyFilterIntensity(16, float.MaxValue) != float.MaxValue ||
            FilterStateEvaluator.ApplyFilterIntensity(31, -float.MaxValue) != -float.MaxValue ||
            FilterStateEvaluator.ApplyFilterIntensity(7, 0.5f) != 2f ||
            Math.Abs(FilterStateEvaluator.ApplyFilterIntensity(10, 0.5f) - 0.95f) > 1e-6f ||
            Math.Abs(FilterStateEvaluator.ApplyFilterIntensity(19, 0.5f)) > 1e-6f)
        {
            throw new InvalidOperationException(
                "SetFilter finite saturation or normal intensity conversion regressed.");
        }

        NativeFilterState state = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 0,
                    Filter = 16, TargetIntensity = float.MaxValue, Enabled = 1
                }
            }, 0.0)[16];
        if (!state.Enabled || !float.IsFinite(state.Intensity) ||
            state.AppliedValue != float.MaxValue)
        {
            throw new InvalidOperationException(
                "SetFilter state evaluation passed an overflowing applied value to native.");
        }
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
