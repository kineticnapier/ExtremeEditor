using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class ScreenTileTimelineBuilder
{
    internal static NativeScreenTileEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level,
            timingMap,
            static action => string.Equals(action.EventType, "ScreenTile", StringComparison.Ordinal));

        return timeline.Occurrences
            .Where(static occurrence => occurrence.Active)
            .Select(ToNative)
            .ToArray();
    }

    private static NativeScreenTileEvent ToNative(VfxOccurrence occurrence)
    {
        (float x, float y) = ReadTile(occurrence.SourceEvent.PropertyOverrides);
        double duration = Math.Max(0.0, occurrence.DurationSeconds ?? 0.0);
        bool identity = Math.Abs(x - 1f) <= 1e-6f && Math.Abs(y - 1f) <= 1e-6f;
        uint flags = duration <= 1e-9
            ? NativeScreenTileEvent.FlagInstantPreservesExistingTween
            : identity
                ? NativeScreenTileEvent.FlagDisableOnComplete
                : NativeScreenTileEvent.FlagEnableBeforeTween;

        return new NativeScreenTileEvent
        {
            StartTime = occurrence.StartTime,
            DurationSeconds = duration,
            TargetX = x,
            TargetY = y,
            Ease = ResolveEase(occurrence.Ease),
            SourceIndex = occurrence.SourceIndex,
            Flags = flags
        };
    }

    private static (float X, float Y) ReadTile(JsonObject? properties)
    {
        if (properties?["tile"] is not JsonArray { Count: >= 2 } tile)
            return (1f, 1f);
        return (ReadFloat(tile[0], 1f), ReadFloat(tile[1], 1f));
    }

    private static float ReadFloat(JsonNode? node, float fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out double number))
                return (float)number;
            if (value.TryGetValue(out string? text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return (float)number;
        }
        return fallback;
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

internal static class ScreenTileStateEvaluator
{
    internal static NativeScreenTileState Evaluate(
        IReadOnlyList<NativeScreenTileEvent> events,
        double chartTime)
    {
        float x = 1f;
        float y = 1f;
        bool enabled = false;

        foreach (NativeScreenTileEvent item in events)
        {
            if (item.StartTime > chartTime)
                break;

            if (item.DurationSeconds <= 1e-9)
            {
                x = item.TargetX;
                y = item.TargetY;
                enabled = Math.Abs(x - 1f) > 1e-6f || Math.Abs(y - 1f) > 1e-6f;
                continue;
            }

            double progress = Math.Clamp(
                (chartTime - item.StartTime) / item.DurationSeconds,
                0.0,
                1.0);
            if ((item.Flags & NativeScreenTileEvent.FlagEnableBeforeTween) != 0)
                enabled = true;
            x = (float)(x + (item.TargetX - x) * progress);
            y = (float)(y + (item.TargetY - y) * progress);
            if (progress >= 1.0 &&
                (item.Flags & NativeScreenTileEvent.FlagDisableOnComplete) != 0)
            {
                enabled = false;
            }
        }

        return new NativeScreenTileState(enabled, x, y);
    }
}
