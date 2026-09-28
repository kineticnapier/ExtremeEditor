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
    public (double X, double Y) Position => (PositionX, PositionY);
    public (double X, double Y) Scale => (ScaleX, ScaleY);

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
            if (TryReadPair(properties!["position"], out double newPositionX, out double newPositionY))
            {
                positionX.Start(newPositionX, startTime, durationSeconds, occurrence.Ease);
                positionY.Start(newPositionY, startTime, durationSeconds, occurrence.Ease);
            }
            else if (TryReadPair(
                         properties["positionOffset"],
                         out double positionOffsetX,
                         out double positionOffsetY))
            {
                string? relativeTo = ReadString(properties["relativeTo"]);
                bool isLastPosition = string.Equals(
                    relativeTo,
                    "LastPosition",
                    StringComparison.OrdinalIgnoreCase);

                // Stock ffxMoveDecorationsPlus captures pivotPosVec for
                // LastPosition before replacing an existing position tween.
                // Start then reproduces Kill(complete: true), so the captured
                // target base and the new tween's start value may differ.
                double targetBaseX = isLastPosition
                    ? positionX.ValueAt(startTime)
                    : state.PositionX;
                double targetBaseY = isLastPosition
                    ? positionY.ValueAt(startTime)
                    : state.PositionY;
                positionX.Start(targetBaseX + positionOffsetX, startTime, durationSeconds, occurrence.Ease);
                positionY.Start(targetBaseY + positionOffsetY, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadDouble(properties["rotation"], out double newRotation) ||
                TryReadDouble(properties["rotationOffset"], out newRotation))
            {
                rotation.Start(newRotation, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadPair(properties["scale"], out double newScaleX, out double newScaleY))
            {
                scaleX.Start(newScaleX, startTime, durationSeconds, occurrence.Ease);
                scaleY.Start(newScaleY, startTime, durationSeconds, occurrence.Ease);
            }

            if (TryReadDouble(properties["opacity"], out double newOpacity))
                opacity.Start(newOpacity, startTime, durationSeconds, occurrence.Ease);
        }

        return state with
        {
            PositionX = positionX.ValueAt(timeSeconds),
            PositionY = positionY.ValueAt(timeSeconds),
            Rotation = rotation.ValueAt(timeSeconds),
            ScaleX = scaleX.ValueAt(timeSeconds),
            ScaleY = scaleY.ValueAt(timeSeconds),
            Opacity = opacity.ValueAt(timeSeconds)
        };
    }

    private static DecorationState FromDecoration(LevelDecoration decoration)
    {
        JsonObject properties = decoration.Properties;
        (double positionX, double positionY) = ReadPair(properties["position"], 0, 0);
        (double scaleX, double scaleY) = ReadPair(properties["scale"], 100, 100);
        return new DecorationState(
            decoration.SourceIndex,
            positionX,
            positionY,
            ReadDouble(properties["rotation"], 0),
            scaleX,
            scaleY,
            ReadDouble(properties["opacity"], 100));
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
