using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct TrackTransformVector2(double? X, double? Y);

internal sealed record TrackTransformSourceData(
    bool DefaultStickToFloors,
    TrackTransformSourceEvent[] Events);

internal sealed record TrackTransformSourceEvent(
    int SourceIndex,
    int Floor,
    string EventType,
    bool Active,
    TrackTransformVector2? PositionOffset,
    TrackTileReference? RelativeTo,
    double? Rotation,
    double? RotationOffset,
    double? StaticScale,
    TrackTransformVector2? MoveScale,
    double? Opacity,
    bool JustThisTile,
    bool EditorOnly,
    bool? StickToFloors,
    TrackTileReference? StartTile,
    TrackTileReference? EndTile,
    int GapLength,
    double Duration,
    double AngleOffset,
    string Ease,
    uint DisabledFlags);

internal readonly record struct StaticTrackTransform(
    float X,
    float Y,
    float Rotation,
    float ScaleX,
    float ScaleY,
    float Opacity,
    bool StickToFloors);

internal static class TrackTransformMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, TrackTransformSourceData> Cache = new();

    internal static void Attach(LevelDocument level, TrackTransformSourceData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static TrackTransformSourceData Get(LevelDocument level) =>
        Cache.TryGetValue(level, out TrackTransformSourceData? data)
            ? data
            : TrackTransformSourceReader.DefaultData;
}

internal static class TrackTransformSourceReader
{
    internal const uint DisablePosition = 1u << 0;
    internal const uint DisableRotation = 1u << 1;
    internal const uint DisableScale = 1u << 2;
    internal const uint DisableOpacity = 1u << 3;
    internal const uint DisableStickToFloors = 1u << 4;

    internal static readonly TrackTransformSourceData DefaultData = new(true, []);

    internal static TrackTransformSourceData Load(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DefaultData;

        const int bufferSize = 1024 * 1024;
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.SequentialScan
        });

        Span<byte> prefix = stackalloc byte[3];
        int prefixLength = stream.Read(prefix);
        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
            return DefaultData;

        bool utf8Bom = prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
        stream.Position = utf8Bom ? 3 : 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var parser = new Parser();
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            int buffered = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffered == buffer.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                buffered += read;
                bool final = read == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
                while (reader.Read())
                    parser.Accept(ref reader);

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                int remaining = buffered - consumed;
                if (remaining > 0 && consumed > 0)
                    buffer.AsSpan(consumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (!final)
                    continue;
                if (buffered != 0)
                    throw new JsonException("Incomplete JSON token at end of track-transform pass.");
                return parser.Complete();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Parser
    {
        private Mode _mode = Mode.Root;
        private Mode _resumeMode;
        private RootField _rootField;
        private Field _field;
        private ActionBuilder _action;
        private int _nextSourceIndex;
        private int _skipDepth;
        private bool _defaultStickToFloors = true;

        private Field _arrayTarget;
        private int _arrayIndex;
        private double? _arrayX;
        private double? _arrayY;
        private int _referenceOffset;
        private string _referenceMode = "ThisTile";

        private string? _disabledProperty;
        private readonly List<TrackTransformSourceEvent> _events = [];

        internal TrackTransformSourceData Complete() => new(
            _defaultStickToFloors,
            _events.ToArray());

        internal void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == Mode.Skip)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    _skipDepth++;
                else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                    _skipDepth--;
                if (_skipDepth == 0)
                    _mode = _resumeMode;
                return;
            }

            switch (_mode)
            {
                case Mode.Root:
                    AcceptRoot(ref reader);
                    break;
                case Mode.Settings:
                    AcceptSettings(ref reader);
                    break;
                case Mode.Actions:
                    AcceptActions(ref reader);
                    break;
                case Mode.Action:
                    AcceptAction(ref reader);
                    break;
                case Mode.Array:
                    AcceptArray(ref reader);
                    break;
                case Mode.Disabled:
                    AcceptDisabled(ref reader);
                    break;
            }
        }

        private void AcceptRoot(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _rootField = reader.ValueTextEquals("settings"u8)
                    ? RootField.Settings
                    : reader.ValueTextEquals("actions"u8)
                        ? RootField.Actions
                        : RootField.Other;
                return;
            }

            RootField field = _rootField;
            _rootField = RootField.None;
            if (field == RootField.Settings && reader.TokenType == JsonTokenType.StartObject)
                _mode = Mode.Settings;
            else if (field == RootField.Actions && reader.TokenType == JsonTokenType.StartArray)
                _mode = Mode.Actions;
            else if (field != RootField.None && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                BeginSkip(Mode.Root);
        }

        private void AcceptSettings(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                _mode = Mode.Root;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _field = reader.ValueTextEquals("stickToFloors"u8) ? Field.StickToFloors : Field.Other;
                return;
            }

            Field field = _field;
            _field = Field.None;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                BeginSkip(Mode.Settings);
                return;
            }

            if (field == Field.StickToFloors)
                _defaultStickToFloors = ReadEnabled(ref reader, true);
        }

        private void AcceptActions(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = Mode.Root;
                return;
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new ActionBuilder
                {
                    SourceIndex = _nextSourceIndex++,
                    Active = true,
                    Ease = "Linear",
                    Duration = 1.0
                };
                _field = Field.None;
                _mode = Mode.Action;
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                BeginSkip(Mode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                FinishAction();
                _mode = Mode.Actions;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _field = MatchField(ref reader);
                return;
            }

            Field field = _field;
            _field = Field.None;
            if (field == Field.None)
                return;

            if (reader.TokenType == JsonTokenType.StartArray && IsArrayField(field))
            {
                BeginArray(field);
                return;
            }
            if (field == Field.Disabled && reader.TokenType == JsonTokenType.StartObject)
            {
                _disabledProperty = null;
                _mode = Mode.Disabled;
                return;
            }
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(Mode.Action);
                return;
            }

            switch (field)
            {
                case Field.Floor when TryReadInt(ref reader, out int floor):
                    _action.Floor = floor;
                    _action.HasFloor = true;
                    break;
                case Field.EventType:
                    _action.EventType = ReadString(ref reader);
                    break;
                case Field.Active:
                    _action.Active = ReadEnabled(ref reader, true);
                    break;
                case Field.Rotation when TryReadDouble(ref reader, out double rotation):
                    _action.Rotation = rotation;
                    break;
                case Field.RotationOffset when TryReadDouble(ref reader, out double rotationOffset):
                    _action.RotationOffset = rotationOffset;
                    break;
                case Field.Scale when TryReadDouble(ref reader, out double scale):
                    _action.StaticScale = scale;
                    _action.MoveScale = new TrackTransformVector2(scale, scale);
                    break;
                case Field.Opacity when TryReadDouble(ref reader, out double opacity):
                    _action.Opacity = opacity;
                    break;
                case Field.JustThisTile:
                    _action.JustThisTile = ReadEnabled(ref reader, false);
                    break;
                case Field.EditorOnly:
                    _action.EditorOnly = ReadEnabled(ref reader, false);
                    break;
                case Field.StickToFloors:
                    _action.StickToFloors = ReadEnabled(ref reader, true);
                    break;
                case Field.GapLength when TryReadInt(ref reader, out int gap):
                    _action.GapLength = Math.Max(0, gap);
                    break;
                case Field.Duration when TryReadDouble(ref reader, out double duration):
                    _action.Duration = Math.Max(0.0, duration);
                    break;
                case Field.AngleOffset when TryReadDouble(ref reader, out double angleOffset):
                    _action.AngleOffset = angleOffset;
                    break;
                case Field.Ease:
                    _action.Ease = ReadString(ref reader) ?? _action.Ease;
                    break;
            }
        }

        private void AcceptArray(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (_arrayTarget is Field.RelativeTo or Field.StartTile or Field.EndTile)
                {
                    var reference = new TrackTileReference(_referenceOffset, _referenceMode);
                    if (_arrayTarget == Field.RelativeTo) _action.RelativeTo = reference;
                    if (_arrayTarget == Field.StartTile) _action.StartTile = reference;
                    if (_arrayTarget == Field.EndTile) _action.EndTile = reference;
                }
                else
                {
                    var pair = new TrackTransformVector2(_arrayX, _arrayY);
                    if (_arrayTarget == Field.PositionOffset) _action.PositionOffset = pair;
                    if (_arrayTarget == Field.Scale) _action.MoveScale = pair;
                }
                _mode = Mode.Action;
                return;
            }

            if (_arrayTarget is Field.RelativeTo or Field.StartTile or Field.EndTile)
            {
                if (_arrayIndex == 0 && TryReadInt(ref reader, out int offset))
                    _referenceOffset = offset;
                else if (_arrayIndex == 1)
                    _referenceMode = ReadString(ref reader) ?? "ThisTile";
            }
            else
            {
                double? component = reader.TokenType == JsonTokenType.Null
                    ? null
                    : TryReadDouble(ref reader, out double value) ? value : null;
                if (_arrayIndex == 0) _arrayX = component;
                if (_arrayIndex == 1) _arrayY = component;
            }
            _arrayIndex++;
        }

        private void AcceptDisabled(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _disabledProperty is null)
            {
                _mode = Mode.Action;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _disabledProperty = reader.GetString();
                return;
            }
            if (_disabledProperty is null)
                return;

            bool disabled = ReadEnabled(ref reader, false);
            if (disabled)
            {
                _action.DisabledFlags |= _disabledProperty switch
                {
                    "positionOffset" => DisablePosition,
                    "rotation" or "rotationOffset" => DisableRotation,
                    "scale" => DisableScale,
                    "opacity" => DisableOpacity,
                    "stickToFloors" => DisableStickToFloors,
                    _ => 0u
                };
            }
            _disabledProperty = null;
        }

        private void FinishAction()
        {
            if (!_action.HasFloor || _action.EventType is not ("PositionTrack" or "MoveTrack"))
                return;

            _events.Add(new TrackTransformSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.EventType,
                _action.Active,
                _action.PositionOffset,
                _action.RelativeTo,
                _action.Rotation,
                _action.RotationOffset,
                _action.StaticScale,
                _action.MoveScale,
                _action.Opacity,
                _action.JustThisTile,
                _action.EditorOnly,
                _action.StickToFloors,
                _action.StartTile,
                _action.EndTile,
                _action.GapLength,
                _action.Duration,
                _action.AngleOffset,
                _action.Ease ?? "Linear",
                _action.DisabledFlags));
        }

        private void BeginArray(Field target)
        {
            _arrayTarget = target;
            _arrayIndex = 0;
            _arrayX = null;
            _arrayY = null;
            _referenceOffset = 0;
            _referenceMode = "ThisTile";
            _mode = Mode.Array;
        }

        private void BeginSkip(Mode resume)
        {
            _resumeMode = resume;
            _skipDepth = 1;
            _mode = Mode.Skip;
        }

        private static bool IsArrayField(Field field) =>
            field is Field.PositionOffset or Field.RelativeTo or Field.Scale or Field.StartTile or Field.EndTile;

        private static Field MatchField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return Field.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return Field.EventType;
            if (reader.ValueTextEquals("active"u8)) return Field.Active;
            if (reader.ValueTextEquals("positionOffset"u8)) return Field.PositionOffset;
            if (reader.ValueTextEquals("relativeTo"u8)) return Field.RelativeTo;
            if (reader.ValueTextEquals("rotation"u8)) return Field.Rotation;
            if (reader.ValueTextEquals("rotationOffset"u8)) return Field.RotationOffset;
            if (reader.ValueTextEquals("scale"u8)) return Field.Scale;
            if (reader.ValueTextEquals("opacity"u8)) return Field.Opacity;
            if (reader.ValueTextEquals("justThisTile"u8)) return Field.JustThisTile;
            if (reader.ValueTextEquals("editorOnly"u8)) return Field.EditorOnly;
            if (reader.ValueTextEquals("stickToFloors"u8)) return Field.StickToFloors;
            if (reader.ValueTextEquals("startTile"u8)) return Field.StartTile;
            if (reader.ValueTextEquals("endTile"u8)) return Field.EndTile;
            if (reader.ValueTextEquals("gapLength"u8)) return Field.GapLength;
            if (reader.ValueTextEquals("duration"u8)) return Field.Duration;
            if (reader.ValueTextEquals("angleOffset"u8)) return Field.AngleOffset;
            if (reader.ValueTextEquals("ease"u8)) return Field.Ease;
            if (reader.ValueTextEquals("disabled"u8)) return Field.Disabled;
            return Field.Other;
        }

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            value = default;
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetInt32(out value);
            return reader.TokenType == JsonTokenType.String &&
                   int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            value = default;
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetDouble(out value);
            return reader.TokenType == JsonTokenType.String &&
                   double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string? ReadString(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            _ => null
        };

        private static bool ReadEnabled(ref Utf8JsonReader reader, bool fallback)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            string? text = ReadString(ref reader);
            if (bool.TryParse(text, out bool parsed)) return parsed;
            if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private enum Mode { Root, Settings, Actions, Action, Array, Disabled, Skip }
        private enum RootField { None, Settings, Actions, Other }
        private enum Field
        {
            None,
            Floor,
            EventType,
            Active,
            PositionOffset,
            RelativeTo,
            Rotation,
            RotationOffset,
            Scale,
            Opacity,
            JustThisTile,
            EditorOnly,
            StickToFloors,
            StartTile,
            EndTile,
            GapLength,
            Duration,
            AngleOffset,
            Ease,
            Disabled,
            Other
        }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public TrackTransformVector2? PositionOffset;
            public TrackTileReference? RelativeTo;
            public double? Rotation;
            public double? RotationOffset;
            public double? StaticScale;
            public TrackTransformVector2? MoveScale;
            public double? Opacity;
            public bool JustThisTile;
            public bool EditorOnly;
            public bool? StickToFloors;
            public TrackTileReference? StartTile;
            public TrackTileReference? EndTile;
            public int GapLength;
            public double Duration;
            public double AngleOffset;
            public string? Ease;
            public uint DisabledFlags;
        }
    }
}

internal static class TrackTransformResolver
{
    private const float TileSize = PathBuilder.DefaultLongTileSize;
    private const float DegToRad = MathF.PI / 180f;

    internal static StaticTrackTransform[] ResolveStatic(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        int count = level.FloorCount;
        if (count == 0)
            return [];

        TrackTransformSourceData source = TrackTransformMetadataCache.Get(level);
        List<TrackTransformSourceEvent> live = BuildLiveEvents(level, source);

        var position = level.Positions.ToArray();
        var rotation = new float[count];
        var scale = Enumerable.Repeat(1f, count).ToArray();
        var opacity = Enumerable.Repeat(1f, count).ToArray();
        var stick = Enumerable.Repeat(source.DefaultStickToFloors, count).ToArray();
        Vector2 persistentOffset = Vector2.Zero;

        foreach (TrackTransformSourceEvent item in live)
        {
            if (!item.Active || item.EventType != "PositionTrack" || (uint)item.Floor >= (uint)count)
                continue;

            int floor = item.Floor;
            if (item.PositionOffset is TrackTransformVector2 offset &&
                (item.DisabledFlags & TrackTransformSourceReader.DisablePosition) == 0u)
            {
                int target = ResolveReference(item.RelativeTo, floor, count);
                float dx = (float)(offset.X ?? 0.0) * TileSize;
                float dy = (float)(offset.Y ?? 0.0) * TileSize;
                if (target != floor)
                {
                    Vector2 baseCurrent = level.Positions[floor] + persistentOffset;
                    Vector2 relativeDelta = position[target] - baseCurrent;
                    dx += relativeDelta.X;
                    dy += relativeDelta.Y;
                }

                var delta = new Vector2(dx, dy);
                if (item.JustThisTile)
                {
                    position[floor] += delta;
                }
                else
                {
                    for (int i = floor; i < count; i++)
                        position[i] += delta;
                    persistentOffset = position[floor] - level.Positions[floor];
                }
            }

            if (item.StaticScale is double rawScale &&
                (item.DisabledFlags & TrackTransformSourceReader.DisableScale) == 0u)
            {
                float value = (float)(rawScale / 100.0);
                ApplyFrom(scale, floor, value, item.JustThisTile);
            }

            if (item.Rotation is double rawRotation &&
                (item.DisabledFlags & TrackTransformSourceReader.DisableRotation) == 0u)
            {
                float value = (float)rawRotation * DegToRad;
                ApplyFrom(rotation, floor, value, item.JustThisTile);
            }

            if (item.Opacity is double rawOpacity &&
                (item.DisabledFlags & TrackTransformSourceReader.DisableOpacity) == 0u)
            {
                float value = Math.Clamp((float)(rawOpacity / 100.0), 0f, 100f);
                ApplyFrom(opacity, floor, value, item.JustThisTile);
            }

            if (item.StickToFloors is bool stickValue &&
                (item.DisabledFlags & TrackTransformSourceReader.DisableStickToFloors) == 0u)
            {
                ApplyFrom(stick, floor, stickValue, item.JustThisTile);
            }
        }

        var result = new StaticTrackTransform[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new StaticTrackTransform(
                position[i].X,
                position[i].Y,
                rotation[i],
                scale[i],
                scale[i],
                opacity[i],
                stick[i]);
        }
        return result;
    }

    internal static NativeTrackTransformEvent[] BuildMoveTimeline(
        LevelDocument level,
        TimingMap timingMap,
        StaticTrackTransform[] staticTransforms)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        if (level.FloorCount == 0 || timingMap.Floors.Count == 0)
            return [];

        TrackTransformSourceData source = TrackTransformMetadataCache.Get(level);
        List<TrackTransformSourceEvent> live = BuildLiveEvents(level, source);
        var perFloor = new Dictionary<int, List<PendingMove>>();

        foreach (TrackTransformSourceEvent item in live)
        {
            if (!item.Active || item.EventType != "MoveTrack" || (uint)item.Floor >= (uint)timingMap.Floors.Count)
                continue;

            FloorTiming timing = timingMap.Floors[item.Floor];
            double bpm = timing.Bpm > 0.0 ? timing.Bpm : Math.Max(level.InitialBpm, 0.000001);
            double beatSeconds = 60.0 / bpm;
            double startTime = timing.EntryTime + item.AngleOffset / 180.0 * beatSeconds;
            double duration = Math.Max(0.0, item.Duration) * beatSeconds;
            int start = ResolveReference(item.StartTile, item.Floor, level.FloorCount);
            int end = ResolveReference(item.EndTile, item.Floor, level.FloorCount);
            if (end < start) (start, end) = (end, start);
            int stride = Math.Max(1, item.GapLength + 1);

            for (int floor = start; floor <= end; floor += stride)
            {
                if (!perFloor.TryGetValue(floor, out List<PendingMove>? list))
                {
                    list = [];
                    perFloor.Add(floor, list);
                }
                list.Add(new PendingMove(item, startTime, duration));
            }
        }

        var output = new List<NativeTrackTransformEvent>();
        foreach ((int floor, List<PendingMove> moves) in perFloor.OrderBy(static pair => pair.Key))
        {
            moves.Sort(static (a, b) =>
            {
                int time = a.StartTime.CompareTo(b.StartTime);
                return time != 0 ? time : a.Source.SourceIndex.CompareTo(b.Source.SourceIndex);
            });

            StaticTrackTransform baseTransform = (uint)floor < (uint)staticTransforms.Length
                ? staticTransforms[floor]
                : new StaticTrackTransform(level.Positions[floor].X, level.Positions[floor].Y, 0f, 1f, 1f, 1f, true);

            var x = new Channel(baseTransform.X);
            var y = new Channel(baseTransform.Y);
            var rotation = new Channel(baseTransform.Rotation);
            var scaleX = new Channel(baseTransform.ScaleX);
            var scaleY = new Channel(baseTransform.ScaleY);
            var opacity = new Channel(baseTransform.Opacity);

            foreach (PendingMove move in moves)
            {
                TrackTransformSourceEvent item = move.Source;
                bool usePosition = item.PositionOffset is TrackTransformVector2 &&
                                   (item.DisabledFlags & TrackTransformSourceReader.DisablePosition) == 0u;
                bool useRotation = item.RotationOffset is double &&
                                   (item.DisabledFlags & TrackTransformSourceReader.DisableRotation) == 0u;
                bool useScale = item.MoveScale is TrackTransformVector2 &&
                                (item.DisabledFlags & TrackTransformSourceReader.DisableScale) == 0u;
                bool useOpacity = item.Opacity is double &&
                                  (item.DisabledFlags & TrackTransformSourceReader.DisableOpacity) == 0u;

                TrackTransformVector2 positionOffset = item.PositionOffset ?? default;
                TrackTransformVector2 moveScale = item.MoveScale ?? default;
                bool useX = usePosition && positionOffset.X is double;
                bool useY = usePosition && positionOffset.Y is double;
                bool useScaleX = useScale && moveScale.X is double;
                bool useScaleY = useScale && moveScale.Y is double;

                uint flags = 0u;
                if (useX) { x.KillComplete(); flags |= NativeTrackTransformEvent.FlagX; }
                if (useY) { y.KillComplete(); flags |= NativeTrackTransformEvent.FlagY; }
                if (useRotation) { rotation.KillComplete(); flags |= NativeTrackTransformEvent.FlagRotation; }
                if (useScaleX) { scaleX.KillComplete(); flags |= NativeTrackTransformEvent.FlagScaleX; }
                if (useScaleY) { scaleY.KillComplete(); flags |= NativeTrackTransformEvent.FlagScaleY; }
                if (useOpacity) { opacity.KillComplete(); flags |= NativeTrackTransformEvent.FlagOpacity; }
                if (flags == 0u)
                    continue;

                float targetX = useX
                    ? baseTransform.X + (float)positionOffset.X!.Value * TileSize
                    : x.Current;
                float targetY = useY
                    ? baseTransform.Y + (float)positionOffset.Y!.Value * TileSize
                    : y.Current;
                float targetRotation = useRotation
                    ? baseTransform.Rotation + (float)item.RotationOffset!.Value * DegToRad
                    : rotation.Current;
                float targetScaleX = useScaleX
                    ? (float)(moveScale.X!.Value / 100.0)
                    : scaleX.Current;
                float targetScaleY = useScaleY
                    ? (float)(moveScale.Y!.Value / 100.0)
                    : scaleY.Current;
                float targetOpacity = useOpacity
                    ? Math.Clamp((float)(item.Opacity!.Value / 100.0), 0f, 100f)
                    : opacity.Current;

                output.Add(new NativeTrackTransformEvent
                {
                    StartTime = move.StartTime,
                    DurationSeconds = move.Duration,
                    Floor = floor,
                    Flags = flags,
                    StartX = x.Current,
                    StartY = y.Current,
                    TargetX = targetX,
                    TargetY = targetY,
                    StartRotation = rotation.Current,
                    TargetRotation = targetRotation,
                    StartScaleX = scaleX.Current,
                    StartScaleY = scaleY.Current,
                    TargetScaleX = targetScaleX,
                    TargetScaleY = targetScaleY,
                    StartOpacity = opacity.Current,
                    TargetOpacity = targetOpacity,
                    Ease = MapEase(item.Ease)
                });

                if (useX) x.Start(targetX, move.Duration);
                if (useY) y.Start(targetY, move.Duration);
                if (useRotation) rotation.Start(targetRotation, move.Duration);
                if (useScaleX) scaleX.Start(targetScaleX, move.Duration);
                if (useScaleY) scaleY.Start(targetScaleY, move.Duration);
                if (useOpacity) opacity.Start(targetOpacity, move.Duration);
            }
        }

        output.Sort(static (a, b) =>
        {
            int floor = a.Floor.CompareTo(b.Floor);
            if (floor != 0) return floor;
            return a.StartTime.CompareTo(b.StartTime);
        });
        return output.ToArray();
    }

    internal static List<TrackTransformSourceEvent> BuildLiveEvents(
        LevelDocument level,
        TrackTransformSourceData source)
    {
        var bySource = source.Events.ToDictionary(static item => item.SourceIndex);
        var fallback = source.Events
            .GroupBy(static item => (item.Floor, item.EventType))
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<TrackTransformSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var used = new HashSet<int>();
        var result = new List<TrackTransformSourceEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (action.EventType is not ("PositionTrack" or "MoveTrack"))
                continue;

            TrackTransformSourceEvent? sourceEvent = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out TrackTransformSourceEvent? indexed))
            {
                sourceEvent = indexed;
                used.Add(indexed.SourceIndex);
            }
            else if (fallback.TryGetValue((action.Floor, action.EventType), out Queue<TrackTransformSourceEvent>? queue))
            {
                while (queue.Count > 0 && used.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    sourceEvent = queue.Dequeue();
                    used.Add(sourceEvent.SourceIndex);
                }
            }

            TrackTransformSourceEvent live = sourceEvent is null
                ? NewDefault(action)
                : sourceEvent with
                {
                    SourceIndex = action.SourceIndex,
                    Floor = action.Floor,
                    Active = action.Active,
                    Duration = action.Duration ?? sourceEvent.Duration,
                    AngleOffset = action.AngleOffset ?? sourceEvent.AngleOffset
                };

            if (action.PropertyOverrides is JsonObject overrides)
                live = Overlay(live, overrides, action);
            result.Add(live);
        }

        // The generic flat parser should retain these events. Keep source-only
        // leftovers as a compatibility fallback for older/loose charts.
        foreach (TrackTransformSourceEvent item in source.Events)
        {
            if (!used.Contains(item.SourceIndex))
                result.Add(item);
        }

        result.Sort(static (a, b) =>
        {
            int floor = a.Floor.CompareTo(b.Floor);
            return floor != 0 ? floor : a.SourceIndex.CompareTo(b.SourceIndex);
        });
        return result;
    }

    private static TrackTransformSourceEvent NewDefault(LevelAction action) => new(
        action.SourceIndex,
        action.Floor,
        action.EventType,
        action.Active,
        null,
        action.EventType == "PositionTrack" ? new TrackTileReference(0, "ThisTile") : null,
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        null,
        action.EventType == "MoveTrack" ? new TrackTileReference(0, "ThisTile") : null,
        action.EventType == "MoveTrack" ? new TrackTileReference(0, "ThisTile") : null,
        0,
        action.Duration ?? 1.0,
        action.AngleOffset ?? 0.0,
        "Linear",
        0u);

    private static TrackTransformSourceEvent Overlay(
        TrackTransformSourceEvent source,
        JsonObject obj,
        LevelAction action)
    {
        TrackTransformVector2? scaleVector = GetVector(obj["scale"]);
        double? scaleScalar = GetDouble(obj, "scale");
        return source with
        {
            Floor = action.Floor,
            Active = action.Active,
            PositionOffset = GetVector(obj["positionOffset"]) ?? source.PositionOffset,
            RelativeTo = GetReference(obj["relativeTo"]) ?? source.RelativeTo,
            Rotation = GetDouble(obj, "rotation") ?? source.Rotation,
            RotationOffset = GetDouble(obj, "rotationOffset") ?? source.RotationOffset,
            StaticScale = scaleScalar ?? source.StaticScale,
            MoveScale = scaleVector ?? (scaleScalar is double scalar ? new TrackTransformVector2(scalar, scalar) : source.MoveScale),
            Opacity = GetDouble(obj, "opacity") ?? source.Opacity,
            JustThisTile = GetBool(obj, "justThisTile") ?? source.JustThisTile,
            EditorOnly = GetBool(obj, "editorOnly") ?? source.EditorOnly,
            StickToFloors = GetEnabled(obj["stickToFloors"]) ?? source.StickToFloors,
            StartTile = GetReference(obj["startTile"]) ?? source.StartTile,
            EndTile = GetReference(obj["endTile"]) ?? source.EndTile,
            GapLength = Math.Max(0, GetInt(obj, "gapLength") ?? source.GapLength),
            Duration = Math.Max(0.0, GetDouble(obj, "duration") ?? action.Duration ?? source.Duration),
            AngleOffset = GetDouble(obj, "angleOffset") ?? action.AngleOffset ?? source.AngleOffset,
            Ease = GetString(obj, "ease") ?? source.Ease,
            DisabledFlags = GetDisabledFlags(obj["disabled"]) ?? source.DisabledFlags
        };
    }

    private static int ResolveReference(TrackTileReference? reference, int floor, int count)
    {
        int target = reference?.Mode switch
        {
            "Start" or "1" => reference.Offset,
            "End" or "2" => count - 1 + reference.Offset,
            _ => floor + (reference?.Offset ?? 0)
        };
        return Math.Clamp(target, 0, Math.Max(0, count - 1));
    }

    private static void ApplyFrom<T>(T[] values, int floor, T value, bool justThisTile)
    {
        int end = justThisTile ? floor + 1 : values.Length;
        for (int i = floor; i < end; i++)
            values[i] = value;
    }

    private static uint MapEase(string? ease)
    {
        string normalized = (ease ?? "Linear").Replace(".easeNone", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized switch
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

    private static TrackTransformVector2? GetVector(JsonNode? node)
    {
        if (node is not JsonArray pair)
            return null;
        double? x = pair.Count > 0 ? GetDouble(pair[0]) : null;
        double? y = pair.Count > 1 ? GetDouble(pair[1]) : null;
        return new TrackTransformVector2(x, y);
    }

    private static TrackTileReference? GetReference(JsonNode? node)
    {
        if (node is not JsonArray { Count: >= 2 } pair)
            return null;
        int? offset = GetInt(pair[0]);
        string? mode = pair[1] is JsonValue second && second.TryGetValue(out string? text) ? text : null;
        return offset is int value ? new TrackTileReference(value, mode ?? "ThisTile") : null;
    }

    private static uint? GetDisabledFlags(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;
        uint flags = 0u;
        foreach ((string name, JsonNode? value) in obj)
        {
            if (GetEnabled(value) != true)
                continue;
            flags |= name switch
            {
                "positionOffset" => TrackTransformSourceReader.DisablePosition,
                "rotation" or "rotationOffset" => TrackTransformSourceReader.DisableRotation,
                "scale" => TrackTransformSourceReader.DisableScale,
                "opacity" => TrackTransformSourceReader.DisableOpacity,
                "stickToFloors" => TrackTransformSourceReader.DisableStickToFloors,
                _ => 0u
            };
        }
        return flags;
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int? GetInt(JsonObject obj, string name) => GetInt(obj[name]);

    private static int? GetInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out int result)) return result;
        return value.TryGetValue(out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static double? GetDouble(JsonObject obj, string name) => GetDouble(obj[name]);

    private static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out double result)) return result;
        if (value.TryGetValue(out int integer)) return integer;
        return value.TryGetValue(out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static bool? GetBool(JsonObject obj, string name) => GetEnabled(obj[name]);

    private static bool? GetEnabled(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out bool result)) return result;
        if (!value.TryGetValue(out string? text)) return null;
        if (bool.TryParse(text, out result)) return result;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private readonly record struct PendingMove(
        TrackTransformSourceEvent Source,
        double StartTime,
        double Duration);

    private sealed class Channel(float initial)
    {
        internal float Current = initial;
        internal float Target = initial;
        internal bool TweenActive;

        internal void KillComplete()
        {
            if (TweenActive)
                Current = Target;
            TweenActive = false;
        }

        internal void Start(float target, double duration)
        {
            Target = target;
            if (duration <= 1e-9)
            {
                Current = target;
                TweenActive = false;
            }
            else
            {
                TweenActive = true;
            }
        }
    }
}
