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
        JsonObject? properties = occurrence.SourceEvent.PropertyOverrides;
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
        if (properties?[name] is not JsonValue value)
            return fallback / 100f;
        if (value.TryGetValue(out double number))
            return (float)(number / 100.0);
        if (value.TryGetValue(out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return (float)(number / 100.0);
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
        var intensity = new float[41];
        var enabled = new bool[41];

        foreach (NativeFilterEvent item in events)
        {
            if (item.StartTime > chartTime || item.Filter >= 41)
                continue;

            int index = checked((int)item.Filter);
            if ((item.Flags & NativeFilterEvent.FlagDisableOthers) != 0)
            {
                for (int i = 0; i < enabled.Length; i++)
                    if (i != index)
                        enabled[i] = false;
            }

            double progress = item.DurationSeconds <= 1e-9
                ? 1.0
                : Math.Clamp((chartTime - item.StartTime) / item.DurationSeconds, 0.0, 1.0);
            intensity[index] = (float)(intensity[index] +
                (item.TargetIntensity - intensity[index]) * progress);
            enabled[index] = item.Enabled != 0;
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

    internal static float ApplyFilterIntensity(uint filter, float intensity) => filter switch
    {
        3 => 0.212f * intensity,
        7 => MathF.Round(5f * intensity),
        8 => 0.5f * intensity,
        10 => 0.9f + 0.1f * intensity,
        11 => 3f * intensity,
        13 => 4f * intensity,
        14 => 10f * intensity,
        16 => 32f * intensity,
        17 => 0.375f * intensity,
        19 => 0.04f * intensity - 0.02f,
        27 => 2f * intensity,
        31 => 20f * intensity,
        33 => 1f + intensity,
        38 => 2.4f * intensity,
        _ => intensity
    };
}
