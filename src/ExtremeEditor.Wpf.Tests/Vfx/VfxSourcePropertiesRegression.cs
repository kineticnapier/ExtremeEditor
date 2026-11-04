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
            VerifyFilterInterruptionMatchesNative();
            VerifyFilterNonlinearEaseMatchesNative();
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

    private static void VerifyFilterNonlinearEaseMatchesNative()
    {
        // Independent reference values from the native ApplyFlashEase formulas.
        var samples = new (uint Ease, double Time, double Expected)[]
        {
            (NativeCameraEvent.EaseInQuad, 0.25, 0.0625),
            (NativeCameraEvent.EaseOutQuad, 0.25, 0.4375),
            (NativeCameraEvent.EaseInOutSine, 0.25, (1.0 - Math.Cos(Math.PI / 4.0)) / 2.0),
            (NativeCameraEvent.EaseOutBounce, 0.5, 0.765625)
        };
        foreach (var (ease, time, expected) in samples)
        {
            NativeFilterState state = FilterStateEvaluator.Evaluate(
                new[]
                {
                    new NativeFilterEvent
                    {
                        StartTime = 0, DurationSeconds = 4,
                        Filter = 0, TargetIntensity = 1f, Enabled = 1, Ease = ease
                    }
                }, 4.0 * time)[0];
            if (!state.Enabled || Math.Abs(state.Intensity - expected) > 1e-6)
                throw new InvalidOperationException(
                    $"SetFilter ease {ease} differed from native at {time}: {state.Intensity} (expected {expected}).");
        }

        uint[] allEases =
        [
            NativeCameraEvent.EaseLinear,
            NativeCameraEvent.EaseInSine, NativeCameraEvent.EaseOutSine, NativeCameraEvent.EaseInOutSine,
            NativeCameraEvent.EaseInQuad, NativeCameraEvent.EaseOutQuad, NativeCameraEvent.EaseInOutQuad,
            NativeCameraEvent.EaseInCubic, NativeCameraEvent.EaseOutCubic, NativeCameraEvent.EaseInOutCubic,
            NativeCameraEvent.EaseInQuart, NativeCameraEvent.EaseOutQuart, NativeCameraEvent.EaseInOutQuart,
            NativeCameraEvent.EaseInQuint, NativeCameraEvent.EaseOutQuint, NativeCameraEvent.EaseInOutQuint,
            NativeCameraEvent.EaseInExpo, NativeCameraEvent.EaseOutExpo, NativeCameraEvent.EaseInOutExpo,
            NativeCameraEvent.EaseInCirc, NativeCameraEvent.EaseOutCirc, NativeCameraEvent.EaseInOutCirc,
            NativeCameraEvent.EaseInBack, NativeCameraEvent.EaseOutBack, NativeCameraEvent.EaseInOutBack,
            NativeCameraEvent.EaseInElastic, NativeCameraEvent.EaseOutElastic, NativeCameraEvent.EaseInOutElastic,
            NativeCameraEvent.EaseInBounce, NativeCameraEvent.EaseOutBounce, NativeCameraEvent.EaseInOutBounce
        ];
        foreach (uint ease in allEases)
        {
            foreach (double time in new[] { 0.0, 0.37, 1.0 })
            {
                double value = NativeFilterEaseEvaluator.Evaluate(ease, time);
                if (!double.IsFinite(value) ||
                    (time == 0.0 && Math.Abs(value) > 1e-10) ||
                    (time == 1.0 && Math.Abs(value - 1.0) > 1e-10))
                {
                    throw new InvalidOperationException(
                        $"SetFilter ease {ease} returned an invalid endpoint or value at {time}: {value}.");
                }
            }
        }

        double back = NativeFilterEaseEvaluator.Evaluate(NativeCameraEvent.EaseOutBack, 0.75);
        if (back <= 1.0 || NativeFilterEaseEvaluator.Evaluate(uint.MaxValue, 0.25) != 0.25)
            throw new InvalidOperationException("SetFilter overshoot or unknown-ease fallback regressed.");

        NativeFilterState saturated = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 4,
                    Filter = 0, TargetIntensity = float.MaxValue, Enabled = 1,
                    Ease = NativeCameraEvent.EaseOutBack
                }
            }, 3.0)[0];
        if (saturated.Intensity != float.MaxValue)
            throw new InvalidOperationException("SetFilter eased overshoot exceeded the finite float range.");
    }

    private static void VerifyFilterInterruptionMatchesNative()
    {
        // The native renderer completes the interrupted tween to its target
        // before beginning the next one, rather than sampling it at chartTime.
        NativeFilterState interrupted = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 4,
                    Filter = 0, TargetIntensity = 1f, Enabled = 1
                },
                new NativeFilterEvent
                {
                    StartTime = 2, DurationSeconds = 2,
                    Filter = 0, TargetIntensity = 0f, Enabled = 1
                }
            }, 3.0)[0];
        if (!interrupted.Enabled || Math.Abs(interrupted.Intensity - 0.5f) > 1e-6f)
            throw new InvalidOperationException(
                $"SetFilter interruption differed from native: {interrupted.Intensity} (expected 0.5).");

        // LED starts at the stock default of 1 when it was disabled.
        NativeFilterState led = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 2,
                    Filter = 7, TargetIntensity = 0f, Enabled = 1
                }
            }, 1.0)[7];
        if (!led.Enabled || Math.Abs(led.Intensity - 0.5f) > 1e-6f)
            throw new InvalidOperationException("SetFilter tween did not start from the stock default.");

        // disableOthers completes pending tweens before disabling them.
        NativeFilterState[] disabled = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 4,
                    Filter = 0, TargetIntensity = 1f, Enabled = 1
                },
                new NativeFilterEvent
                {
                    StartTime = 2, DurationSeconds = 0,
                    Filter = 1, TargetIntensity = 0.25f, Enabled = 1,
                    Flags = NativeFilterEvent.FlagDisableOthers
                }
            }, 3.0);
        if (disabled[0].Enabled || disabled[0].Intensity != 1f ||
            !disabled[1].Enabled || disabled[1].Intensity != 0.25f)
            throw new InvalidOperationException("SetFilter disableOthers did not complete the prior tween.");

        // Invalid native inputs must not poison valid events or a default state.
        NativeFilterState[] invalidClock = FilterStateEvaluator.Evaluate(
            new[]
            {
                new NativeFilterEvent
                {
                    StartTime = 0, DurationSeconds = 0,
                    Filter = 7, TargetIntensity = 0f, Enabled = 1
                }
            }, double.NaN);
        if (invalidClock[7].Enabled || invalidClock[7].Intensity != 1f)
            throw new InvalidOperationException("SetFilter invalid clock did not preserve defaults.");
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
