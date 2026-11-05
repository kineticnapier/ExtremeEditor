using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class FilterTimelineBuilder
{
    private static readonly Dictionary<string, uint> FilterIds = new(StringComparer.Ordinal)
    {
        ["Grayscale"] = 0, ["Sepia"] = 1, ["Invert"] = 2, ["VHS"] = 3,
        ["EightiesTV"] = 4, ["FiftiesTV"] = 5, ["Arcade"] = 6, ["LED"] = 7,
        ["Rain"] = 8, ["Blizzard"] = 9, ["PixelSnow"] = 10, ["Compression"] = 11,
        ["Glitch"] = 12, ["Pixelate"] = 13, ["Waves"] = 14, ["Static"] = 15,
        ["Grain"] = 16, ["MotionBlur"] = 17, ["Fisheye"] = 18, ["Aberration"] = 19,
        ["Drawing"] = 20, ["Neon"] = 21, ["Handheld"] = 22, ["NightVision"] = 23,
        ["Funk"] = 24, ["Tunnel"] = 25, ["Weird3D"] = 26, ["Blur"] = 27,
        ["BlurFocus"] = 28, ["GaussianBlur"] = 29, ["HexagonBlack"] = 30,
        ["Posterize"] = 31, ["Sharpen"] = 32, ["Contrast"] = 33,
        ["EdgeBlackLine"] = 34, ["OilPaint"] = 35, ["SuperDot"] = 36,
        ["WaterDrop"] = 37, ["LightWater"] = 38, ["Petals"] = 39,
        ["PetalsInstant"] = 40
    };

    internal static NativeFilterEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level,
            timingMap,
            static action => string.Equals(action.EventType, "SetFilter", StringComparison.Ordinal));

        return timeline.Occurrences
            .Where(static occurrence => occurrence.Active)
            .Select(ToNative)
            .Where(static item => item.Filter != uint.MaxValue)
            .ToArray();
    }

    private static NativeFilterEvent ToNative(VfxOccurrence occurrence)
    {
        // Edited values win; otherwise the values loaded from the chart.
        JsonObject? properties = MoveDecorationsTargeting.GetProperties(occurrence.SourceEvent);
        string name = ReadString(properties, "filter", "Grayscale");
        uint filter = FilterIds.TryGetValue(name, out uint id) ? id : uint.MaxValue;

        return new NativeFilterEvent
        {
            StartTime = occurrence.StartTime,
            DurationSeconds = Math.Max(0.0, occurrence.DurationSeconds ?? 0.0),
            Filter = filter,
            TargetIntensity = ReadPercent(properties, "intensity", 100f),
            Enabled = ReadBool(properties, "enabled", true) ? 1u : 0u,
            Ease = ResolveEase(occurrence.Ease),
            SourceIndex = occurrence.SourceIndex,
            Flags = ReadBool(properties, "disableOthers", false)
                ? NativeFilterEvent.FlagDisableOthers
                : 0u
        };
    }

    private static string ReadString(JsonObject? properties, string name, string fallback) =>
        properties?[name] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? fallback
            : fallback;

    private static bool ReadBool(JsonObject? properties, string name, bool fallback)
    {
        if (properties?[name] is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out bool result))
            return result;
        return value.TryGetValue(out string? text) && bool.TryParse(text, out result)
            ? result
            : fallback;
    }

    private static float ReadPercent(JsonObject? properties, string name, float fallback)
    {
        if (properties?[name] is JsonValue value &&
            (value.TryGetValue(out double number) ||
             (value.TryGetValue(out string? text) &&
              double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))))
        {
            double scaled = number / 100.0;
            if (double.IsFinite(scaled) && Math.Abs(scaled) <= float.MaxValue)
                return (float)scaled;
        }
        return fallback / 100f;
    }

    private static uint ResolveEase(string? ease) => ease switch
    {
        "InSine" => NativeCameraEvent.EaseInSine,
        "OutSine" => NativeCameraEvent.EaseOutSine,
        "InOutSine" => NativeCameraEvent.EaseInOutSine,
        "InQuad" => NativeCameraEvent.EaseInQuad,
        "OutQuad" => NativeCameraEvent.EaseOutQuad,
        "InOutQuad" => NativeCameraEvent.EaseInOutQuad,
        "InCubic" => NativeCameraEvent.EaseInCubic,
        "OutCubic" => NativeCameraEvent.EaseOutCubic,
        "InOutCubic" => NativeCameraEvent.EaseInOutCubic,
        "InQuart" => NativeCameraEvent.EaseInQuart,
        "OutQuart" => NativeCameraEvent.EaseOutQuart,
        "InOutQuart" => NativeCameraEvent.EaseInOutQuart,
        "InQuint" => NativeCameraEvent.EaseInQuint,
        "OutQuint" => NativeCameraEvent.EaseOutQuint,
        "InOutQuint" => NativeCameraEvent.EaseInOutQuint,
        "InExpo" => NativeCameraEvent.EaseInExpo,
        "OutExpo" => NativeCameraEvent.EaseOutExpo,
        "InOutExpo" => NativeCameraEvent.EaseInOutExpo,
        "InCirc" => NativeCameraEvent.EaseInCirc,
        "OutCirc" => NativeCameraEvent.EaseOutCirc,
        "InOutCirc" => NativeCameraEvent.EaseInOutCirc,
        "InBack" => NativeCameraEvent.EaseInBack,
        "OutBack" => NativeCameraEvent.EaseOutBack,
        "InOutBack" => NativeCameraEvent.EaseInOutBack,
        "InElastic" => NativeCameraEvent.EaseInElastic,
        "OutElastic" => NativeCameraEvent.EaseOutElastic,
        "InOutElastic" => NativeCameraEvent.EaseInOutElastic,
        "InBounce" => NativeCameraEvent.EaseInBounce,
        "OutBounce" => NativeCameraEvent.EaseOutBounce,
        "InOutBounce" => NativeCameraEvent.EaseInOutBounce,
        _ => NativeCameraEvent.EaseLinear
    };
}

internal static class FilterStateEvaluator
{
    internal static NativeFilterState[] Evaluate(IReadOnlyList<NativeFilterEvent> events, double chartTime)
    {
        ArgumentNullException.ThrowIfNull(events);

        var intensity = new float[41];
        var enabled = new bool[41];
        var tweenFrom = new float[41];
        var activeTweens = new NativeFilterEvent?[41];
        for (int i = 0; i < intensity.Length; i++)
            intensity[i] = DefaultIntensity(i);

        if (double.IsFinite(chartTime))
        {
            foreach (NativeFilterEvent item in events)
            {
                if (!double.IsFinite(item.StartTime) ||
                    !double.IsFinite(item.DurationSeconds) ||
                    !float.IsFinite(item.TargetIntensity) ||
                    item.StartTime > chartTime || item.Filter >= 41)
                    continue;

                int index = checked((int)item.Filter);
                if (activeTweens[index] is NativeFilterEvent interrupted)
                {
                    intensity[index] = interrupted.TargetIntensity;
                    enabled[index] = interrupted.Enabled != 0;
                    activeTweens[index] = null;
                }

                if (item.DurationSeconds <= 1e-9)
                {
                    intensity[index] = item.TargetIntensity;
                    enabled[index] = item.Enabled != 0;
                }
                else
                {
                    if (!enabled[index])
                        intensity[index] = DefaultIntensity(index);
                    tweenFrom[index] = intensity[index];
                    activeTweens[index] = item;
                    enabled[index] = item.Enabled != 0;
                }

                if ((item.Flags & NativeFilterEvent.FlagDisableOthers) != 0)
                {
                    for (int other = 0; other < enabled.Length; other++)
                    {
                        if (other == index)
                            continue;
                        if (activeTweens[other] is NativeFilterEvent pending)
                        {
                            intensity[other] = pending.TargetIntensity;
                            activeTweens[other] = null;
                        }
                        enabled[other] = false;
                    }
                }
            }

            for (int i = 0; i < activeTweens.Length; i++)
            {
                if (activeTweens[i] is not NativeFilterEvent tween)
                    continue;
                double raw = Math.Clamp(
                    (chartTime - tween.StartTime) / tween.DurationSeconds, 0.0, 1.0);
                double eased = NativeFilterEaseEvaluator.Evaluate(tween.Ease, raw);
                double progress = double.IsFinite(eased) ? eased : raw;
                double value = (double)tweenFrom[i] +
                    ((double)tween.TargetIntensity - tweenFrom[i]) * progress;
                intensity[i] = (float)Math.Clamp(
                    value, -(double)float.MaxValue, (double)float.MaxValue);
            }
        }

        var result = new NativeFilterState[41];
        for (int i = 0; i < result.Length; i++)
        {
            uint filter = checked((uint)i);
            result[i] = new NativeFilterState(
                filter,
                enabled[i],
                intensity[i],
                ApplyFilterIntensity(filter, intensity[i]));
        }
        return result;
    }

    private static float DefaultIntensity(int filter) => filter switch
    {
        7 or 9 => 1f,
        13 => 0.01f,
        18 or 19 => 0.5f,
        _ => 0f
    };

    internal static float ApplyFilterIntensity(uint filter, float intensity)
    {
        if (!float.IsFinite(intensity))
            return 0f;

        return filter switch
        {
            3 => ScaleFinite(0.212f, intensity),
            7 => MathF.Round(ScaleFinite(5f, intensity), MidpointRounding.AwayFromZero),
            8 => ScaleFinite(0.5f, intensity),
            10 => 0.9f + ScaleFinite(0.1f, intensity),
            11 => ScaleFinite(3f, intensity),
            13 => ScaleFinite(4f, intensity),
            14 => ScaleFinite(10f, intensity),
            16 => ScaleFinite(32f, intensity),
            17 => ScaleFinite(0.375f, intensity),
            19 => ScaleFinite(0.04f, intensity) - 0.02f,
            27 => ScaleFinite(2f, intensity),
            31 => ScaleFinite(20f, intensity),
            33 => 1f + intensity,
            38 => ScaleFinite(2.4f, intensity),
            _ => intensity
        };
    }

    private static float ScaleFinite(float multiplier, float intensity)
    {
        double scaled = (double)multiplier * intensity;
        return (float)Math.Clamp(scaled, -(double)float.MaxValue, (double)float.MaxValue);
    }
}
