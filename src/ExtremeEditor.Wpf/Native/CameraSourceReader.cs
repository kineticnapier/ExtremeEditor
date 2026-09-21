using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal sealed record CameraSourceData(
    CameraSourceEvent[] Events,
    string InitialRelativeTo,
    CameraSourcePosition InitialPosition,
    double InitialRotation,
    double InitialZoom);

internal static class CameraMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, CameraSourceData> Cache = new();

    internal static void Attach(LevelDocument level, CameraSourceData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static CameraSourceData Get(LevelDocument level) =>
        Cache.TryGetValue(level, out CameraSourceData? data)
            ? data
            : CameraSourceReader.DefaultData;
}

/// <summary>
/// Streaming reader for the camera properties whose "missing" state matters.
/// In ADOFAI a missing/null MoveCamera position axis, rotation, or zoom means
/// "do not touch that camera channel"; it must not be replaced by the schema default.
/// </summary>
internal static class CameraSourceReader
{
    internal static readonly CameraSourceData DefaultData = new(
        [],
        "Player",
        new CameraSourcePosition(0.0, 0.0),
        0.0,
        100.0);

    internal static CameraSourceData Load(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DefaultData;

        const int bufferSize = 1024 * 1024;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.SequentialScan
        };

        using var stream = new FileStream(path, options);
        Span<byte> prefix = stackalloc byte[3];
        int prefixLength = stream.Read(prefix);
        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
            return DefaultData;

        bool utf8Bom = prefixLength >= 3 &&
                       prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
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
                bool finalBlock = read == 0;

                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), finalBlock, state);
                while (reader.Read())
                    parser.Accept(ref reader);

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                int remaining = buffered - consumed;
                if (remaining > 0 && consumed > 0)
                    buffer.AsSpan(consumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (!finalBlock)
                    continue;
                if (buffered != 0)
                    throw new JsonException("Incomplete JSON token at end of ADOFAI camera pass.");
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
        private ParserMode _mode = ParserMode.Root;
        private ParserMode _resumeMode;
        private RootField _rootField;
        private SettingField _settingField;
        private ActionField _actionField;
        private ActionBuilder _action;
        private int _skipDepth;
        private int _nextSourceIndex;
        private int _positionIndex;
        private double? _positionX;
        private double? _positionY;
        private bool _readingSettingsPosition;
        private readonly List<CameraSourceEvent> _events = [];

        private string _initialRelativeTo = "Player";
        private CameraSourcePosition _initialPosition = new(0.0, 0.0);
        private double _initialRotation;
        private double _initialZoom = 100.0;

        internal void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == ParserMode.Skip)
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
                case ParserMode.Root:
                    AcceptRoot(ref reader);
                    break;
                case ParserMode.Settings:
                    AcceptSettings(ref reader);
                    break;
                case ParserMode.Actions:
                    AcceptActions(ref reader);
                    break;
                case ParserMode.Action:
                    AcceptAction(ref reader);
                    break;
                case ParserMode.Position:
                    AcceptPosition(ref reader);
                    break;
            }
        }

        internal CameraSourceData Complete() => new(
            _events.ToArray(),
            _initialRelativeTo,
            _initialPosition,
            _initialRotation,
            _initialZoom);

        private void AcceptRoot(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _rootField = reader.ValueTextEquals("settings"u8) ? RootField.Settings :
                    reader.ValueTextEquals("actions"u8) ? RootField.Actions : RootField.Other;
                return;
            }

            if (_rootField == RootField.None)
                return;

            RootField field = _rootField;
            _rootField = RootField.None;
            if (field == RootField.Settings && reader.TokenType == JsonTokenType.StartObject)
                _mode = ParserMode.Settings;
            else if (field == RootField.Actions && reader.TokenType == JsonTokenType.StartArray)
                _mode = ParserMode.Actions;
            else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                BeginSkip(ParserMode.Root);
        }

        private void AcceptSettings(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _settingField == SettingField.None)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _settingField = MatchSetting(ref reader);
                return;
            }

            SettingField field = _settingField;
            _settingField = SettingField.None;
            if (field == SettingField.None)
                return;

            if (field == SettingField.Position && reader.TokenType == JsonTokenType.StartArray)
            {
                BeginPosition(readingSettingsPosition: true);
                return;
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Settings);
                return;
            }

            switch (field)
            {
                case SettingField.RelativeTo:
                    _initialRelativeTo = ReadString(ref reader) ?? _initialRelativeTo;
                    break;
                case SettingField.Rotation when TryReadDouble(ref reader, out double rotation):
                    _initialRotation = rotation;
                    break;
                case SettingField.Zoom when TryReadDouble(ref reader, out double zoom) && zoom > 0.0:
                    _initialZoom = zoom;
                    break;
            }
        }

        private void AcceptActions(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new ActionBuilder
                {
                    SourceIndex = _nextSourceIndex++,
                    Active = true,
                    Duration = 1.0,
                    RelativeTo = "Player",
                    Position = new CameraSourcePosition(null, null),
                    Rotation = null,
                    Zoom = null,
                    Ease = "Linear"
                };
                _actionField = ActionField.None;
                _mode = ParserMode.Action;
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                BeginSkip(ParserMode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _actionField == ActionField.None)
            {
                FinalizeAction();
                _mode = ParserMode.Actions;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _actionField = MatchAction(ref reader);
                return;
            }

            ActionField field = _actionField;
            _actionField = ActionField.None;
            if (field == ActionField.None)
                return;

            if (field == ActionField.Position && reader.TokenType == JsonTokenType.StartArray)
            {
                BeginPosition(readingSettingsPosition: false);
                return;
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Action);
                return;
            }

            switch (field)
            {
                case ActionField.Floor when TryReadInt(ref reader, out int floor):
                    _action.Floor = floor;
                    _action.HasFloor = true;
                    break;
                case ActionField.EventType:
                    _action.EventType = ReadString(ref reader);
                    break;
                case ActionField.Active:
                    _action.Active = ReadBool(ref reader, true);
                    break;
                case ActionField.Duration when TryReadDouble(ref reader, out double duration):
                    _action.Duration = duration;
                    break;
                case ActionField.RelativeTo:
                    _action.RelativeTo = ReadString(ref reader) ?? _action.RelativeTo;
                    break;
                case ActionField.Rotation:
                    _action.Rotation = reader.TokenType == JsonTokenType.Null
                        ? null
                        : TryReadDouble(ref reader, out double rotation) ? rotation : _action.Rotation;
                    break;
                case ActionField.Zoom:
                    _action.Zoom = reader.TokenType == JsonTokenType.Null
                        ? null
                        : TryReadDouble(ref reader, out double zoom) ? zoom : _action.Zoom;
                    break;
                case ActionField.AngleOffset when TryReadDouble(ref reader, out double offset):
                    _action.AngleOffset = offset;
                    break;
                case ActionField.Ease:
                    _action.Ease = ReadString(ref reader) ?? _action.Ease;
                    break;
            }
        }

        private void BeginPosition(bool readingSettingsPosition)
        {
            _positionIndex = 0;
            _positionX = null;
            _positionY = null;
            _readingSettingsPosition = readingSettingsPosition;
            _mode = ParserMode.Position;
        }

        private void AcceptPosition(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                var position = new CameraSourcePosition(_positionX, _positionY);
                if (_readingSettingsPosition)
                {
                    _initialPosition = new CameraSourcePosition(
                        position.X ?? _initialPosition.X ?? 0.0,
                        position.Y ?? _initialPosition.Y ?? 0.0);
                    _mode = ParserMode.Settings;
                }
                else
                {
                    _action.Position = position;
                    _mode = ParserMode.Action;
                }
                return;
            }

            double? value = reader.TokenType == JsonTokenType.Null
                ? null
                : TryReadDouble(ref reader, out double parsed) ? parsed : null;
            if (_positionIndex == 0)
                _positionX = value;
            else if (_positionIndex == 1)
                _positionY = value;
            _positionIndex++;
        }

        private void FinalizeAction()
        {
            if (!_action.HasFloor ||
                !string.Equals(_action.EventType, "MoveCamera", StringComparison.Ordinal))
            {
                return;
            }

            _events.Add(new CameraSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.Active,
                _action.Duration,
                _action.RelativeTo,
                _action.Position,
                _action.Rotation,
                _action.Zoom,
                _action.AngleOffset,
                _action.Ease));
        }

        private void BeginSkip(ParserMode resumeMode)
        {
            _resumeMode = resumeMode;
            _skipDepth = 1;
            _mode = ParserMode.Skip;
        }

        private static SettingField MatchSetting(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("relativeTo"u8)) return SettingField.RelativeTo;
            if (reader.ValueTextEquals("position"u8)) return SettingField.Position;
            if (reader.ValueTextEquals("rotation"u8)) return SettingField.Rotation;
            if (reader.ValueTextEquals("zoom"u8)) return SettingField.Zoom;
            return SettingField.Other;
        }

        private static ActionField MatchAction(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return ActionField.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return ActionField.EventType;
            if (reader.ValueTextEquals("active"u8)) return ActionField.Active;
            if (reader.ValueTextEquals("duration"u8)) return ActionField.Duration;
            if (reader.ValueTextEquals("relativeTo"u8)) return ActionField.RelativeTo;
            if (reader.ValueTextEquals("position"u8)) return ActionField.Position;
            if (reader.ValueTextEquals("rotation"u8)) return ActionField.Rotation;
            if (reader.ValueTextEquals("zoom"u8)) return ActionField.Zoom;
            if (reader.ValueTextEquals("angleOffset"u8)) return ActionField.AngleOffset;
            if (reader.ValueTextEquals("ease"u8)) return ActionField.Ease;
            return ActionField.Other;
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return double.TryParse(
                    reader.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
            value = 0.0;
            return false;
        }

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return int.TryParse(
                    reader.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
            value = 0;
            return false;
        }

        private static bool ReadBool(ref Utf8JsonReader reader, bool fallback)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            string? text = ReadString(ref reader);
            return bool.TryParse(text, out bool parsed) ? parsed : fallback;
        }

        private static string? ReadString(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            _ => null
        };

        private enum ParserMode { Root, Settings, Actions, Action, Position, Skip }
        private enum RootField { None, Settings, Actions, Other }
        private enum SettingField { None, RelativeTo, Position, Rotation, Zoom, Other }
        private enum ActionField
        {
            None,
            Floor,
            EventType,
            Active,
            Duration,
            RelativeTo,
            Position,
            Rotation,
            Zoom,
            AngleOffset,
            Ease,
            Other
        }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public double Duration;
            public string RelativeTo;
            public CameraSourcePosition Position;
            public double? Rotation;
            public double? Zoom;
            public double AngleOffset;
            public string Ease;
        }
    }
}

internal static class NativeCameraTimelineBuilder
{
    private const double InitialEventTime = -1.0e100;

    private readonly record struct PendingEvent(
        CameraSourceEvent Source,
        double StartTime,
        double DurationSeconds);

    private readonly record struct AxisTarget(bool PlayerRelative, float Value);

    internal static NativeCameraEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        if (level.FloorCount == 0 || timingMap.Floors.Count == 0)
            return [];

        CameraSourceData metadata = CameraMetadataCache.Get(level);
        List<CameraSourceEvent> live = BuildLiveEvents(level, metadata.Events);
        var pending = new List<PendingEvent>(live.Count);
        foreach (CameraSourceEvent item in live)
        {
            if (!item.Active || (uint)item.Floor >= (uint)timingMap.Floors.Count)
                continue;

            FloorTiming timing = timingMap.Floors[item.Floor];
            double bpm = timing.Bpm > 0.0
                ? timing.Bpm
                : Math.Max(0.000001, level.InitialBpm);
            double beatSeconds = 60.0 / bpm;
            double start = timing.EntryTime + item.AngleOffset / 180.0 * beatSeconds;
            double duration = Math.Max(0.0, item.Duration) * beatSeconds;
            pending.Add(new PendingEvent(item, start, duration));
        }

        pending.Sort(static (a, b) =>
        {
            int time = a.StartTime.CompareTo(b.StartTime);
            return time != 0 ? time : a.Source.SourceIndex.CompareTo(b.Source.SourceIndex);
        });

        var output = new List<NativeCameraEvent>(pending.Count + 1)
        {
            BuildInitialEvent(level, metadata)
        };

        foreach (PendingEvent pendingEvent in pending)
        {
            CameraSourceEvent item = pendingEvent.Source;
            var pose = timingMap.GetPose(level, pendingEvent.StartTime);
            float playerX = pose.StationaryPlanet.X;
            float playerY = pose.StationaryPlanet.Y;
            uint flags = 0u;

            var next = new NativeCameraEvent
            {
                StartTime = pendingEvent.StartTime,
                DurationSeconds = pendingEvent.DurationSeconds,
                Ease = ParseEase(item.Ease)
            };

            if (item.Position.X is double x)
            {
                float current = EvaluateAxis(
                    output,
                    pendingEvent.StartTime,
                    playerX,
                    NativeCameraEvent.FlagApplyX,
                    NativeCameraEvent.FlagTargetPlayerX,
                    axisX: true);
                AxisTarget target = ResolveAxis(
                    item.RelativeTo,
                    x,
                    item.Floor,
                    current,
                    level,
                    axisX: true);
                next.StartX = current;
                next.TargetX = target.Value;
                flags |= NativeCameraEvent.FlagApplyX;
                if (target.PlayerRelative)
                    flags |= NativeCameraEvent.FlagTargetPlayerX;
            }

            if (item.Position.Y is double y)
            {
                float current = EvaluateAxis(
                    output,
                    pendingEvent.StartTime,
                    playerY,
                    NativeCameraEvent.FlagApplyY,
                    NativeCameraEvent.FlagTargetPlayerY,
                    axisX: false);
                AxisTarget target = ResolveAxis(
                    item.RelativeTo,
                    y,
                    item.Floor,
                    current,
                    level,
                    axisX: false);
                next.StartY = current;
                next.TargetY = target.Value;
                flags |= NativeCameraEvent.FlagApplyY;
                if (target.PlayerRelative)
                    flags |= NativeCameraEvent.FlagTargetPlayerY;
            }

            if (item.Rotation is double rotation)
            {
                float current = EvaluateScalar(
                    output,
                    pendingEvent.StartTime,
                    NativeCameraEvent.FlagApplyRotation,
                    zoom: false);
                next.StartRotation = current;
                next.TargetRotation = (float)(rotation * Math.PI / 180.0);
                flags |= NativeCameraEvent.FlagApplyRotation;
            }

            if (item.Zoom is double zoom)
            {
                float current = EvaluateScalar(
                    output,
                    pendingEvent.StartTime,
                    NativeCameraEvent.FlagApplyZoom,
                    zoom: true);
                next.StartZoom = current;
                next.TargetZoom = (float)Math.Clamp(zoom * 0.01, 0.01, 100.0);
                flags |= NativeCameraEvent.FlagApplyZoom;
            }

            next.Flags = flags;
            if ((flags & NativeCameraEvent.FlagApplyMask) != 0u)
                output.Add(next);
        }

        return output.ToArray();
    }

    private static NativeCameraEvent BuildInitialEvent(
        LevelDocument level,
        CameraSourceData metadata)
    {
        double initialX = metadata.InitialPosition.X ?? 0.0;
        double initialY = metadata.InitialPosition.Y ?? 0.0;
        AxisTarget x = ResolveAxis(
            metadata.InitialRelativeTo,
            initialX,
            0,
            level.Positions[0].X,
            level,
            axisX: true);
        AxisTarget y = ResolveAxis(
            metadata.InitialRelativeTo,
            initialY,
            0,
            level.Positions[0].Y,
            level,
            axisX: false);

        uint flags = NativeCameraEvent.FlagApplyMask;
        if (x.PlayerRelative) flags |= NativeCameraEvent.FlagTargetPlayerX;
        if (y.PlayerRelative) flags |= NativeCameraEvent.FlagTargetPlayerY;

        return new NativeCameraEvent
        {
            StartTime = InitialEventTime,
            DurationSeconds = 0.0,
            TargetX = x.Value,
            TargetY = y.Value,
            TargetRotation = (float)(metadata.InitialRotation * Math.PI / 180.0),
            TargetZoom = (float)Math.Clamp(metadata.InitialZoom * 0.01, 0.01, 100.0),
            Flags = flags,
            Ease = NativeCameraEvent.EaseLinear
        };
    }

    private static AxisTarget ResolveAxis(
        string relativeTo,
        double value,
        int floor,
        float current,
        LevelDocument level,
        bool axisX)
    {
        float component = (float)value;
        if (string.Equals(relativeTo, "Player", StringComparison.OrdinalIgnoreCase))
            return new AxisTarget(true, component);

        if (string.Equals(relativeTo, "Tile", StringComparison.OrdinalIgnoreCase))
        {
            int safeFloor = Math.Clamp(floor, 0, Math.Max(0, level.Positions.Length - 1));
            float origin = axisX ? level.Positions[safeFloor].X : level.Positions[safeFloor].Y;
            return new AxisTarget(false, origin + component);
        }

        if (string.Equals(relativeTo, "LastPosition", StringComparison.OrdinalIgnoreCase))
            return new AxisTarget(false, current + component);

        if (string.Equals(relativeTo, "Global", StringComparison.OrdinalIgnoreCase))
            return new AxisTarget(false, component);

        return new AxisTarget(true, component);
    }

    private static float EvaluateAxis(
        List<NativeCameraEvent> events,
        double chartTime,
        float player,
        uint applyFlag,
        uint playerFlag,
        bool axisX)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            NativeCameraEvent item = events[i];
            if (item.StartTime > chartTime || (item.Flags & applyFlag) == 0u)
                continue;

            float start = axisX ? item.StartX : item.StartY;
            float target = axisX ? item.TargetX : item.TargetY;
            if ((item.Flags & playerFlag) != 0u)
                target += player;
            return Lerp(start, target, Progress(item, chartTime));
        }

        return player;
    }

    private static float EvaluateScalar(
        List<NativeCameraEvent> events,
        double chartTime,
        uint applyFlag,
        bool zoom)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            NativeCameraEvent item = events[i];
            if (item.StartTime > chartTime || (item.Flags & applyFlag) == 0u)
                continue;

            float start = zoom ? item.StartZoom : item.StartRotation;
            float target = zoom ? item.TargetZoom : item.TargetRotation;
            return Lerp(start, target, Progress(item, chartTime));
        }

        return zoom ? 1.0f : 0.0f;
    }

    private static float Progress(NativeCameraEvent item, double chartTime)
    {
        double progress = item.DurationSeconds <= 1e-9
            ? 1.0
            : Math.Clamp((chartTime - item.StartTime) / item.DurationSeconds, 0.0, 1.0);
        return (float)ApplyEase(item.Ease, progress);
    }

    private static List<CameraSourceEvent> BuildLiveEvents(
        LevelDocument level,
        IReadOnlyList<CameraSourceEvent> sourceEvents)
    {
        var bySource = sourceEvents.ToDictionary(static item => item.SourceIndex);
        var fallback = sourceEvents
            .GroupBy(static item => item.Floor)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<CameraSourceEvent>(
                    group.OrderBy(static item => item.SourceIndex)));
        var usedSources = new HashSet<int>();
        var result = new List<CameraSourceEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (!string.Equals(action.EventType, "MoveCamera", StringComparison.Ordinal))
                continue;

            CameraSourceEvent? sourceEvent = null;
            if (action.SourceIndex >= 0 &&
                bySource.TryGetValue(action.SourceIndex, out CameraSourceEvent? indexed))
            {
                sourceEvent = indexed;
                usedSources.Add(indexed.SourceIndex);
            }
            else if (fallback.TryGetValue(action.Floor, out Queue<CameraSourceEvent>? queue))
            {
                while (queue.Count > 0 && usedSources.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    sourceEvent = queue.Dequeue();
                    usedSources.Add(sourceEvent.SourceIndex);
                }
            }

            CameraSourceEvent live = sourceEvent ?? new CameraSourceEvent(
                action.SourceIndex,
                action.Floor,
                action.Active,
                action.Duration ?? 1.0,
                "Player",
                new CameraSourcePosition(null, null),
                null,
                null,
                action.AngleOffset ?? 0.0,
                "Linear");

            live = live with
            {
                SourceIndex = action.SourceIndex,
                Floor = action.Floor,
                Active = action.Active,
                Duration = action.Duration ?? live.Duration,
                AngleOffset = action.AngleOffset ?? live.AngleOffset
            };

            if (action.PropertyOverrides is JsonObject overrides)
                live = Overlay(live, overrides, action);
            result.Add(live);
        }

        return result;
    }

    private static CameraSourceEvent Overlay(
        CameraSourceEvent source,
        JsonObject obj,
        LevelAction action)
    {
        CameraSourcePosition position = obj.ContainsKey("position")
            ? ReadPosition(obj["position"])
            : source.Position;
        double? rotation = obj.ContainsKey("rotation")
            ? GetDouble(obj["rotation"])
            : source.Rotation;
        double? zoom = obj.ContainsKey("zoom")
            ? GetDouble(obj["zoom"])
            : source.Zoom;

        return source with
        {
            Floor = action.Floor,
            Active = action.Active,
            Duration = GetDouble(obj["duration"]) ?? action.Duration ?? source.Duration,
            RelativeTo = GetString(obj["relativeTo"]) ?? source.RelativeTo,
            Position = position,
            Rotation = rotation,
            Zoom = zoom,
            AngleOffset = GetDouble(obj["angleOffset"]) ?? action.AngleOffset ?? source.AngleOffset,
            Ease = GetString(obj["ease"]) ?? source.Ease
        };
    }

    private static CameraSourcePosition ReadPosition(JsonNode? node)
    {
        if (node is not JsonArray pair)
            return new CameraSourcePosition(null, null);
        return new CameraSourcePosition(
            pair.Count > 0 ? GetDouble(pair[0]) : null,
            pair.Count > 1 ? GetDouble(pair[1]) : null);
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out double result))
            return result;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static uint ParseEase(string? ease) => ease switch
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

    private static double ApplyEase(uint ease, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        const double c1 = 1.70158;
        const double c2 = c1 * 1.525;
        const double c3 = c1 + 1.0;
        double c4 = 2.0 * Math.PI / 3.0;
        double c5 = 2.0 * Math.PI / 4.5;

        return ease switch
        {
            NativeCameraEvent.EaseInSine => 1.0 - Math.Cos(t * Math.PI / 2.0),
            NativeCameraEvent.EaseOutSine => Math.Sin(t * Math.PI / 2.0),
            NativeCameraEvent.EaseInOutSine => -(Math.Cos(Math.PI * t) - 1.0) / 2.0,
            NativeCameraEvent.EaseInQuad => t * t,
            NativeCameraEvent.EaseOutQuad => 1.0 - (1.0 - t) * (1.0 - t),
            NativeCameraEvent.EaseInOutQuad => t < 0.5
                ? 2.0 * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0) / 2.0,
            NativeCameraEvent.EaseInCubic => t * t * t,
            NativeCameraEvent.EaseOutCubic => 1.0 - Math.Pow(1.0 - t, 3.0),
            NativeCameraEvent.EaseInOutCubic => t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
            NativeCameraEvent.EaseInQuart => t * t * t * t,
            NativeCameraEvent.EaseOutQuart => 1.0 - Math.Pow(1.0 - t, 4.0),
            NativeCameraEvent.EaseInOutQuart => t < 0.5
                ? 8.0 * Math.Pow(t, 4.0)
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 4.0) / 2.0,
            NativeCameraEvent.EaseInQuint => Math.Pow(t, 5.0),
            NativeCameraEvent.EaseOutQuint => 1.0 - Math.Pow(1.0 - t, 5.0),
            NativeCameraEvent.EaseInOutQuint => t < 0.5
                ? 16.0 * Math.Pow(t, 5.0)
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 5.0) / 2.0,
            NativeCameraEvent.EaseInExpo => t <= 0.0 ? 0.0 : Math.Pow(2.0, 10.0 * t - 10.0),
            NativeCameraEvent.EaseOutExpo => t >= 1.0 ? 1.0 : 1.0 - Math.Pow(2.0, -10.0 * t),
            NativeCameraEvent.EaseInOutExpo => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
                ? Math.Pow(2.0, 20.0 * t - 10.0) / 2.0
                : (2.0 - Math.Pow(2.0, -20.0 * t + 10.0)) / 2.0,
            NativeCameraEvent.EaseInCirc => 1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - t * t)),
            NativeCameraEvent.EaseOutCirc => Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(t - 1.0, 2.0))),
            NativeCameraEvent.EaseInOutCirc => t < 0.5
                ? (1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(2.0 * t, 2.0)))) / 2.0
                : (Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0))) + 1.0) / 2.0,
            NativeCameraEvent.EaseInBack => c3 * t * t * t - c1 * t * t,
            NativeCameraEvent.EaseOutBack =>
                1.0 + c3 * Math.Pow(t - 1.0, 3.0) + c1 * Math.Pow(t - 1.0, 2.0),
            NativeCameraEvent.EaseInOutBack => t < 0.5
                ? Math.Pow(2.0 * t, 2.0) * ((c2 + 1.0) * 2.0 * t - c2) / 2.0
                : (Math.Pow(2.0 * t - 2.0, 2.0) *
                   ((c2 + 1.0) * (2.0 * t - 2.0) + c2) + 2.0) / 2.0,
            NativeCameraEvent.EaseInElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : -Math.Pow(2.0, 10.0 * t - 10.0) * Math.Sin((t * 10.0 - 10.75) * c4),
            NativeCameraEvent.EaseOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : Math.Pow(2.0, -10.0 * t) * Math.Sin((t * 10.0 - 0.75) * c4) + 1.0,
            NativeCameraEvent.EaseInOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
                ? -(Math.Pow(2.0, 20.0 * t - 10.0) *
                    Math.Sin((20.0 * t - 11.125) * c5)) / 2.0
                : Math.Pow(2.0, -20.0 * t + 10.0) *
                  Math.Sin((20.0 * t - 11.125) * c5) / 2.0 + 1.0,
            NativeCameraEvent.EaseInBounce => 1.0 - OutBounce(1.0 - t),
            NativeCameraEvent.EaseOutBounce => OutBounce(t),
            NativeCameraEvent.EaseInOutBounce => t < 0.5
                ? (1.0 - OutBounce(1.0 - 2.0 * t)) / 2.0
                : (1.0 + OutBounce(2.0 * t - 1.0)) / 2.0,
            _ => t
        };
    }

    private static double OutBounce(double t)
    {
        const double n1 = 7.5625;
        const double d1 = 2.75;
        if (t < 1.0 / d1) return n1 * t * t;
        if (t < 2.0 / d1)
        {
            t -= 1.5 / d1;
            return n1 * t * t + 0.75;
        }
        if (t < 2.5 / d1)
        {
            t -= 2.25 / d1;
            return n1 * t * t + 0.9375;
        }
        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
