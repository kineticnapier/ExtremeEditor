using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeBackgroundState(
    string Color,
    string Image,
    string ImageColor,
    float ParallaxX,
    float ParallaxY,
    string DisplayMode,
    bool ImageSmoothing,
    bool LockRotation,
    bool Loop,
    float ScalingRatio);

internal readonly record struct NativeBackgroundEvent(
    double StartTime,
    int SourceIndex,
    NativeBackgroundState State);

internal readonly record struct NativeBackgroundAsset(uint Id, string ImagePath);

internal static class BackgroundSourceReader
{
    internal static NativeBackgroundState ReadInitial(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (string.IsNullOrWhiteSpace(level.SourcePath) || !File.Exists(level.SourcePath))
            return ReadRoot(null);

        using var stream = File.OpenRead(level.SourcePath);
        JsonNode? root = JsonNode.Parse(stream);
        return ReadRoot(root?["settings"] as JsonObject);
    }

    internal static NativeBackgroundState ReadRoot(JsonObject? settings) => new(
        String(settings, "backgroundColor", "000000"),
        String(settings, "bgImage", ""),
        String(settings, "bgImageColor", "ffffff"),
        Pair(settings, "parallax").X,
        Pair(settings, "parallax").Y,
        String(settings, "bgDisplayMode", "FitToScreen"),
        Bool(settings, "imageSmoothing", true),
        Bool(settings, "lockRot", false),
        Bool(settings, "loopBG", false),
        Number(settings, "scalingRatio", 100f));

    internal static NativeBackgroundState ApplyEvent(NativeBackgroundState previous, JsonObject? properties)
    {
        if (properties is null)
            return previous;
        (float x, float y) parallax = properties.ContainsKey("parallax")
            ? Pair(properties, "parallax")
            : (previous.ParallaxX, previous.ParallaxY);
        return previous with
        {
            Color = String(properties, "color", previous.Color),
            Image = String(properties, "bgImage", previous.Image),
            ImageColor = String(properties, "imageColor", previous.ImageColor),
            ParallaxX = parallax.x,
            ParallaxY = parallax.y,
            DisplayMode = String(properties, "bgDisplayMode", previous.DisplayMode),
            ImageSmoothing = Bool(properties, "imageSmoothing", previous.ImageSmoothing),
            LockRotation = Bool(properties, "lockRot", previous.LockRotation),
            Loop = Bool(properties, "loopBG", previous.Loop),
            ScalingRatio = Number(properties, "scalingRatio", previous.ScalingRatio)
        };
    }

    private static string String(JsonObject? source, string name, string fallback) =>
        source?[name] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? fallback : fallback;

    private static bool Bool(JsonObject? source, string name, bool fallback) =>
        source?[name] is JsonValue value && value.TryGetValue(out bool result)
            ? result : fallback;

    private static float Number(JsonObject? source, string name, float fallback)
    {
        if (source?[name] is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out double number))
            return (float)number;
        if (value.TryGetValue(out int integer))
            return integer;
        return value.TryGetValue(out string? text) &&
               float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result : fallback;
    }

    private static (float X, float Y) Pair(JsonObject? source, string name)
    {
        if (source?[name] is not JsonArray { Count: >= 2 } pair)
            return (100f, 100f);
        return (ReadArrayNumber(pair[0], 100f), ReadArrayNumber(pair[1], 100f));
    }

    private static float ReadArrayNumber(JsonNode? node, float fallback)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out double result))
            return (float)result;
        if (value.TryGetValue(out int integer))
            return integer;
        return fallback;
    }
}

internal static class BackgroundTimelineBuilder
{
    internal static NativeBackgroundEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level, timingMap,
            static action => string.Equals(action.EventType, "CustomBackground", StringComparison.Ordinal));

        NativeBackgroundState state = BackgroundSourceReader.ReadInitial(level);
        var result = new List<NativeBackgroundEvent>();
        foreach (VfxOccurrence occurrence in timeline.Occurrences
                     .Where(static item => item.Active)
                     .OrderBy(static item => item.StartTime - item.StartEffectOffset)
                     .ThenBy(static item => item.Floor))
        {
            state = BackgroundSourceReader.ApplyEvent(state, occurrence.SourceEvent.PropertyOverrides);
            result.Add(new NativeBackgroundEvent(occurrence.StartTime, occurrence.SourceIndex, state));
        }
        return result.ToArray();
    }
}

internal static class BackgroundStateEvaluator
{
    internal static NativeBackgroundState Evaluate(
        NativeBackgroundState initial,
        IReadOnlyList<NativeBackgroundEvent> timeline,
        double chartTime)
    {
        NativeBackgroundState state = initial;
        foreach (NativeBackgroundEvent item in timeline)
        {
            if (item.StartTime <= chartTime)
                state = item.State;
        }
        return state;
    }
}
