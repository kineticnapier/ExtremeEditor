using System.Globalization;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

public sealed record DecorationState(
    int SourceIndex,
    double PositionX,
    double PositionY,
    double Rotation,
    double ScaleX,
    double ScaleY,
    double Opacity)
{
    public double PivotOffsetX { get; init; }
    public double PivotOffsetY { get; init; }
    public double ParallaxOffsetX { get; init; }
    public double ParallaxOffsetY { get; init; }

    public (double X, double Y) Position => (PositionX, PositionY);
    public (double X, double Y) Scale => (ScaleX, ScaleY);
    public (double X, double Y) PivotOffset => (PivotOffsetX, PivotOffsetY);
    public (double X, double Y) ParallaxOffset => (ParallaxOffsetX, ParallaxOffsetY);

    public static DecorationState Evaluate(
        LevelDecoration decoration,
        VfxTimeline timeline,
        double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(timeline);

        DecorationState state = FromDecoration(decoration);
        string? decorationTag = ReadString(decoration.Properties["tag"]);
        if (string.IsNullOrEmpty(decorationTag))
            return state;

        var positionX = new ScalarTweenState(state.PositionX);
        var positionY = new ScalarTweenState(state.PositionY);
        var rotation = new ScalarTweenState(state.Rotation);
        var scaleX = new ScalarTweenState(state.ScaleX);
        var scaleY = new ScalarTweenState(state.ScaleY);
        var opacity = new ScalarTweenState(state.Opacity);
        var pivotOffsetX = new ScalarTweenState(state.PivotOffsetX);
        var pivotOffsetY = new ScalarTweenState(state.PivotOffsetY);
        var parallaxOffsetX = new ScalarTweenState(state.ParallaxOffsetX);
        var parallaxOffsetY = new ScalarTweenState(state.ParallaxOffsetY);

        foreach (VfxOccurrence occurrence in timeline.Occurrences)
        {
            if (occurrence.StartTime > timeSeconds)
                break;
            if (!occurrence.Active ||
                !string.Equals(occurrence.EventType, "MoveDecorations", StringComparison.Ordinal) ||
                occurrence.RepeatPlacementResolved == false ||
                occurrence.Duration is not double)
            {
                continue;
            }

            JsonObject? properties = occurrence.SourceEvent.PropertyOverrides;
            string? targetTag = ReadString(properties?["tag"]);
            if (!TagsOverlap(decorationTag, targetTag))
                continue;

            double durationSeconds = occurrence.DurationSeconds ?? 0;
            if (durationSeconds > 0 && !DotweenEaseEvaluator.IsSupported(occurrence.Ease))
                continue;

            double startTime = occurrence.StartTime;

            if (TryReadComponents(properties!["position"], out double? newPositionX, out double? newPositionY))
            {
                StartIfSpecified(positionX, newPositionX, startTime, durationSeconds, occurrence.Ease);
                StartIfSpecified(positionY, newPositionY, startTime, durationSeconds, occurrence.Ease);
            }
            else if (TryReadComponents(
                         properties["positionOffset"],
                         out double? positionOffsetX,
                         out double? positionOffsetY))
            {
                string? relativeTo = ReadString(properties["relativeTo"]);
                bool isLastPosition = string.Equals(
                    relativeTo,
                    "LastPosition",
                    StringComparison.OrdinalIgnoreCase);

                // Stock ffxMoveDecorationsPlus captures the current position for LastPosition
                // before replacing an existing position tween. Other relative modes target
                // the AddDecoration base plus positionOffset.
                if (positionOffsetX is double offsetX)
                {
                    double targetBaseX = isLastPosition
                        ? positionX.ValueAt(startTime)
                        : state.PositionX;
                    positionX.Start(targetBaseX + offsetX, startTime, durationSeconds, occurrence.Ease);
                }

                if (positionOffsetY is double offsetY)
                {
                    double targetBaseY = isLastPosition
                        ? positionY.ValueAt(startTime)
                        : state.PositionY;
                    positionY.Start(targetBaseY + offsetY, startTime, durationSeconds, occurrence.Ease);
                }
            }

            if (TryReadDouble(properties["rotation"], out double newRotation) ||
                TryReadDouble(properties["rotationOffset"], out newRotation))
            {
                rotation.Start(newRotation, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadComponents(properties["scale"], out double? newScaleX, out double? newScaleY))
            {
                StartIfSpecified(scaleX, newScaleX, startTime, durationSeconds, occurrence.Ease);
                StartIfSpecified(scaleY, newScaleY, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadDouble(properties["opacity"], out double newOpacity))
                opacity.Start(newOpacity, startTime, durationSeconds, occurrence.Ease);

            if (TryReadComponents(properties["pivotOffset"], out double? newPivotX, out double? newPivotY))
            {
                StartIfSpecified(pivotOffsetX, newPivotX, startTime, durationSeconds, occurrence.Ease);
                StartIfSpecified(pivotOffsetY, newPivotY, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadComponents(
                    properties["parallaxOffset"],
                    out double? newParallaxX,
                    out double? newParallaxY))
            {
                StartIfSpecified(parallaxOffsetX, newParallaxX, startTime, durationSeconds, occurrence.Ease);
                StartIfSpecified(parallaxOffsetY, newParallaxY, startTime, durationSeconds, occurrence.Ease);
            }
        }

        return state with
        {
            PositionX = positionX.ValueAt(timeSeconds),
            PositionY = positionY.ValueAt(timeSeconds),
            Rotation = rotation.ValueAt(timeSeconds),
            ScaleX = scaleX.ValueAt(timeSeconds),
            ScaleY = scaleY.ValueAt(timeSeconds),
            Opacity = opacity.ValueAt(timeSeconds),
            PivotOffsetX = pivotOffsetX.ValueAt(timeSeconds),
            PivotOffsetY = pivotOffsetY.ValueAt(timeSeconds),
            ParallaxOffsetX = parallaxOffsetX.ValueAt(timeSeconds),
            ParallaxOffsetY = parallaxOffsetY.ValueAt(timeSeconds)
        };
    }

    private static DecorationState FromDecoration(LevelDecoration decoration)
    {
        JsonObject properties = decoration.Properties;
        (double positionX, double positionY) = ReadPair(properties["position"], 0, 0);
        (double scaleX, double scaleY) = ReadPair(properties["scale"], 100, 100);
        (double pivotOffsetX, double pivotOffsetY) = ReadPair(properties["pivotOffset"], 0, 0);
        (double parallaxOffsetX, double parallaxOffsetY) = ReadPair(properties["parallaxOffset"], 0, 0);

        return new DecorationState(
            decoration.SourceIndex,
            positionX,
            positionY,
            ReadDouble(properties["rotation"], 0),
            scaleX,
            scaleY,
            ReadDouble(properties["opacity"], 100))
        {
            PivotOffsetX = pivotOffsetX,
            PivotOffsetY = pivotOffsetY,
            ParallaxOffsetX = parallaxOffsetX,
            ParallaxOffsetY = parallaxOffsetY
        };
    }

    private static void StartIfSpecified(
        ScalarTweenState tween,
        double? target,
        double startTime,
        double durationSeconds,
        string? ease)
    {
        if (target is double value)
            tween.Start(value, startTime, durationSeconds, ease);
    }

    private static bool TagsOverlap(string decorationTags, string? targetTags)
    {
        if (string.IsNullOrEmpty(targetTags))
            return false;

        string[] decorationTokens = SplitTags(decorationTags);
        string[] targetTokens = SplitTags(targetTags);
        foreach (string target in targetTokens)
        {
            foreach (string candidate in decorationTokens)
            {
                if (string.Equals(target, candidate, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static string[] SplitTags(string tags) =>
        tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return null;
    }

    private static (double X, double Y) ReadPair(
        JsonNode? node,
        double defaultX,
        double defaultY) =>
        TryReadPair(node, out double x, out double y) ? (x, y) : (defaultX, defaultY);

    private static bool TryReadPair(JsonNode? node, out double x, out double y)
    {
        if (node is JsonArray { Count: >= 2 } array &&
            TryReadDouble(array[0], out x) &&
            TryReadDouble(array[1], out y))
        {
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static bool TryReadComponents(JsonNode? node, out double? x, out double? y)
    {
        x = null;
        y = null;
        if (node is not JsonArray { Count: >= 2 } array)
            return false;

        if (array[0] is not null)
        {
            if (!TryReadDouble(array[0], out double valueX))
                return false;
            x = valueX;
        }

        if (array[1] is not null)
        {
            if (!TryReadDouble(array[1], out double valueY))
                return false;
            y = valueY;
        }

        return true;
    }

    private static double ReadDouble(JsonNode? node, double defaultValue) =>
        TryReadDouble(node, out double value) ? value : defaultValue;

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out value) && double.IsFinite(value))
                return true;
            if (jsonValue.TryGetValue(out float single) && float.IsFinite(single))
            {
                value = single;
                return true;
            }
            if (double.TryParse(
                    node.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value) &&
                double.IsFinite(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private sealed class ScalarTweenState
    {
        private double _value;
        private ScalarTween? _active;

        public ScalarTweenState(double value)
        {
            _value = value;
        }

        public void Start(
            double target,
            double startTime,
            double durationSeconds,
            string? ease)
        {
            AdvanceTo(startTime);
            if (_active is not null)
            {
                // ffxMoveDecorationsPlus keeps one tween per property and
                // replaces it with Kill(complete: true).
                _value = _active.Target;
                _active = null;
            }

            if (durationSeconds <= 0)
            {
                _value = target;
                return;
            }

            _active = new ScalarTween(_value, target, startTime, durationSeconds, ease);
        }

        public double ValueAt(double timeSeconds)
        {
            if (_active is null)
                return _value;

            if (!DotweenEaseEvaluator.TryEvaluate(
                    _active.Ease,
                    timeSeconds - _active.StartTime,
                    _active.DurationSeconds,
                    out float progress))
            {
                return _value;
            }
            return Lerp(_active.Start, _active.Target, progress);
        }

        private void AdvanceTo(double timeSeconds)
        {
            if (_active is null)
                return;

            if (timeSeconds >= _active.StartTime + _active.DurationSeconds)
            {
                _value = _active.Target;
                _active = null;
            }
        }
    }

    private sealed record ScalarTween(
        double Start,
        double Target,
        double StartTime,
        double DurationSeconds,
        string? Ease);

    private static double Lerp(double start, double end, double progress) =>
        start + (end - start) * progress;
}
