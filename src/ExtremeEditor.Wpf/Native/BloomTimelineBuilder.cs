using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class BloomTimelineBuilder
{
    internal static NativeBloomEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level,
            timingMap,
            static action => string.Equals(action.EventType, "Bloom", StringComparison.Ordinal));

        return timeline.Occurrences
            .Where(static occurrence => occurrence.Active)
            .Select(ToNative)
            .ToArray();
    }

    private static NativeBloomEvent ToNative(VfxOccurrence occurrence)
    {
        // Edited values win; otherwise the values loaded from the chart.
        JsonObject? properties = MoveDecorationsTargeting.GetProperties(occurrence.SourceEvent);
        return new NativeBloomEvent
        {
            StartTime = occurrence.StartTime,
            DurationSeconds = Math.Max(0.0, occurrence.DurationSeconds ?? 0.0),
            Threshold = ReadPercent(properties, "threshold", 0f),
            Intensity = ReadPercent(properties, "intensity", 0f),
            Tint = ReadColor(properties, "color", 0xffffffu),
            Enabled = ReadBool(properties, "enabled", true) ? 1u : 0u,
            Ease = ResolveEase(occurrence.Ease),
            SourceIndex = occurrence.SourceIndex
        };
    }

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

    private static string? ReadString(JsonObject? properties, string name)
    {
        if (properties?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return text;
        return value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
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

    private static uint ReadColor(JsonObject? properties, string name, uint fallback)
    {
        string? text = ReadString(properties, name);
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        string hex = text.Trim().TrimStart('#');
        if (hex.Length >= 6)
            hex = hex[..6];
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value & 0xffffffu
            : fallback;
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

internal static class BloomStateEvaluator
{
    internal static NativeBloomState Evaluate(
        IReadOnlyList<NativeBloomEvent> events,
        double chartTime)
    {
        NativeBloomState state = new(false, 0f, 0f, 0xffffffu);
        if (!double.IsFinite(chartTime))
            return state;
        NativeBloomEvent? previous = null;

        foreach (NativeBloomEvent item in events)
        {
            if (!double.IsFinite(item.StartTime) ||
                !double.IsFinite(item.DurationSeconds) ||
                !float.IsFinite(item.Threshold) ||
                !float.IsFinite(item.Intensity))
                continue;
            if (item.StartTime > chartTime)
                break;

            NativeBloomEvent? before = previous;
            float startThreshold = before?.Threshold ?? item.Threshold;
            float startIntensity = before?.Intensity ?? item.Intensity;
            uint startTint = before?.Tint ?? item.Tint;
            double raw = item.DurationSeconds <= 1e-9
                ? 1.0
                : (chartTime - item.StartTime) / item.DurationSeconds;
            double eased = NativeFilterEaseEvaluator.Evaluate(item.Ease, raw);
            double progress = double.IsFinite(eased) ? eased : 0.0;

            static float InterpolateFinite(float from, float to, double t)
            {
                double value = (double)from + ((double)to - from) * t;
                return (float)Math.Clamp(value, -(double)float.MaxValue, (double)float.MaxValue);
            }

            state = new NativeBloomState(
                item.Enabled != 0,
                InterpolateFinite(startThreshold, item.Threshold, progress),
                InterpolateFinite(startIntensity, item.Intensity, progress),
                LerpColor(startTint, item.Tint, progress));
            previous = item;
        }

        return state;
    }

    private static uint LerpColor(uint start, uint end, double t)
    {
        static int Channel(uint value, int shift) => (int)((value >> shift) & 0xffu);
        int r = (int)Math.Round(Channel(start, 16) + (Channel(end, 16) - Channel(start, 16)) * t, MidpointRounding.AwayFromZero);
        int g = (int)Math.Round(Channel(start, 8) + (Channel(end, 8) - Channel(start, 8)) * t, MidpointRounding.AwayFromZero);
        int b = (int)Math.Round(Channel(start, 0) + (Channel(end, 0) - Channel(start, 0)) * t, MidpointRounding.AwayFromZero);
        return ((uint)Math.Clamp(r, 0, 255) << 16) |
               ((uint)Math.Clamp(g, 0, 255) << 8) |
               (uint)Math.Clamp(b, 0, 255);
    }
}
