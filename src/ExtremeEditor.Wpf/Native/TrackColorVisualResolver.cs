using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal sealed record TrackColorSourceData(
    TrackColorStyle InitialStyle,
    TrackColorSourceEvent[] Events);

internal sealed record TrackColorStyle(
    string ColorType,
    string PrimaryColor,
    string SecondaryColor,
    int PulseLength);

internal sealed record TrackTileReference(int Offset, string Mode);

internal sealed record TrackColorSourceEvent(
    int SourceIndex,
    int Floor,
    string EventType,
    bool Active,
    string? ColorType,
    string? PrimaryColor,
    string? SecondaryColor,
    int? PulseLength,
    bool JustThisTile,
    int GapLength,
    TrackTileReference? StartTile,
    TrackTileReference? EndTile);

internal static class TrackColorMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, TrackColorSourceData> Cache = new();

    internal static void Attach(LevelDocument level, TrackColorSourceData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static TrackColorSourceData Get(LevelDocument level) =>
        Cache.TryGetValue(level, out TrackColorSourceData? data)
            ? data
            : TrackColorSourceReader.DefaultData;
}

internal static class TrackColorSourceReader
{
    internal static readonly TrackColorSourceData DefaultData = new(
        new TrackColorStyle("Single", "debb7b", "ffffff", 10),
        []);

    internal static TrackColorSourceData Load(string path, CancellationToken cancellationToken = default)
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
                    return DefaultData;
                return parser.Complete();
            }
        }
        catch (JsonException)
        {
            return DefaultData;
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
        private ActionField _tileReferenceTarget;
        private int _tileReferenceIndex;
        private int _tileReferenceOffset;
        private string _tileReferenceMode = "ThisTile";
        private int _skipDepth;
        private int _nextSourceIndex;

        private string _trackColorType = "Single";
        private string _trackColor = "debb7b";
        private string _secondaryTrackColor = "ffffff";
        private int _trackPulseLength = 10;
        private readonly List<TrackColorSourceEvent> _events = [];

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
                    AcceptSetting(ref reader);
                    break;
                case ParserMode.Actions:
                    AcceptActions(ref reader);
                    break;
                case ParserMode.Action:
                    AcceptAction(ref reader);
                    break;
                case ParserMode.TileReference:
                    AcceptTileReference(ref reader);
                    break;
            }
        }

        internal TrackColorSourceData Complete() => new(
            new TrackColorStyle(_trackColorType, _trackColor, _secondaryTrackColor, _trackPulseLength),
            _events.ToArray());

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

        private void AcceptSetting(ref Utf8JsonReader reader)
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
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Settings);
                return;
            }

            switch (field)
            {
                case SettingField.TrackColorType:
                    _trackColorType = ReadString(ref reader) ?? _trackColorType;
                    break;
                case SettingField.TrackColor:
                    _trackColor = ReadString(ref reader) ?? _trackColor;
                    break;
                case SettingField.SecondaryTrackColor:
                    _secondaryTrackColor = ReadString(ref reader) ?? _secondaryTrackColor;
                    break;
                case SettingField.TrackPulseLength when TryReadInt(ref reader, out int pulse):
                    _trackPulseLength = Math.Max(1, pulse);
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
                    GapLength = 0
                };
                _actionField = ActionField.None;
                _mode = ParserMode.Action;
            }
            else if (reader.TokenType is JsonTokenType.StartArray)
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

            if (reader.TokenType == JsonTokenType.StartArray && field is ActionField.StartTile or ActionField.EndTile)
            {
                _tileReferenceTarget = field;
                _tileReferenceIndex = 0;
                _tileReferenceOffset = 0;
                _tileReferenceMode = "ThisTile";
                _mode = ParserMode.TileReference;
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
                case ActionField.TrackColorType:
                    _action.ColorType = ReadString(ref reader);
                    break;
                case ActionField.TrackColor:
                    _action.PrimaryColor = ReadString(ref reader);
                    break;
                case ActionField.SecondaryTrackColor:
                    _action.SecondaryColor = ReadString(ref reader);
                    break;
                case ActionField.TrackPulseLength when TryReadInt(ref reader, out int pulse):
                    _action.PulseLength = pulse;
                    break;
                case ActionField.JustThisTile:
                    _action.JustThisTile = ReadBool(ref reader, false);
                    break;
                case ActionField.GapLength when TryReadInt(ref reader, out int gap):
                    _action.GapLength = Math.Max(0, gap);
                    break;
            }
        }

        private void AcceptTileReference(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                var reference = new TrackTileReference(_tileReferenceOffset, _tileReferenceMode);
                if (_tileReferenceTarget == ActionField.StartTile)
                    _action.StartTile = reference;
                else if (_tileReferenceTarget == ActionField.EndTile)
                    _action.EndTile = reference;
                _mode = ParserMode.Action;
                return;
            }

            if (_tileReferenceIndex == 0 && TryReadInt(ref reader, out int offset))
                _tileReferenceOffset = offset;
            else if (_tileReferenceIndex == 1)
                _tileReferenceMode = ReadString(ref reader) ?? "ThisTile";
            _tileReferenceIndex++;
        }

        private void FinalizeAction()
        {
            if (!_action.HasFloor || _action.EventType is not ("ColorTrack" or "RecolorTrack"))
                return;

            _events.Add(new TrackColorSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.EventType,
                _action.Active,
                _action.ColorType,
                _action.PrimaryColor,
                _action.SecondaryColor,
                _action.PulseLength,
                _action.JustThisTile,
                _action.GapLength,
                _action.StartTile,
                _action.EndTile));
        }

        private void BeginSkip(ParserMode resumeMode)
        {
            _resumeMode = resumeMode;
            _skipDepth = 1;
            _mode = ParserMode.Skip;
        }

        private static SettingField MatchSetting(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("trackColorType"u8)) return SettingField.TrackColorType;
            if (reader.ValueTextEquals("trackColor"u8)) return SettingField.TrackColor;
            if (reader.ValueTextEquals("secondaryTrackColor"u8)) return SettingField.SecondaryTrackColor;
            if (reader.ValueTextEquals("trackPulseLength"u8)) return SettingField.TrackPulseLength;
            return SettingField.Other;
        }

        private static ActionField MatchAction(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return ActionField.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return ActionField.EventType;
            if (reader.ValueTextEquals("active"u8)) return ActionField.Active;
            if (reader.ValueTextEquals("trackColorType"u8)) return ActionField.TrackColorType;
            if (reader.ValueTextEquals("trackColor"u8)) return ActionField.TrackColor;
            if (reader.ValueTextEquals("secondaryTrackColor"u8)) return ActionField.SecondaryTrackColor;
            if (reader.ValueTextEquals("trackPulseLength"u8)) return ActionField.TrackPulseLength;
            if (reader.ValueTextEquals("justThisTile"u8)) return ActionField.JustThisTile;
            if (reader.ValueTextEquals("gapLength"u8)) return ActionField.GapLength;
            if (reader.ValueTextEquals("startTile"u8)) return ActionField.StartTile;
            if (reader.ValueTextEquals("endTile"u8)) return ActionField.EndTile;
            return ActionField.Other;
        }

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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

        private enum ParserMode { Root, Settings, Actions, Action, TileReference, Skip }
        private enum RootField { None, Settings, Actions, Other }
        private enum SettingField { None, TrackColorType, TrackColor, SecondaryTrackColor, TrackPulseLength, Other }
        private enum ActionField
        {
            None,
            Floor,
            EventType,
            Active,
            TrackColorType,
            TrackColor,
            SecondaryTrackColor,
            TrackPulseLength,
            JustThisTile,
            GapLength,
            StartTile,
            EndTile,
            Other
        }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public string? ColorType;
            public string? PrimaryColor;
            public string? SecondaryColor;
            public int? PulseLength;
            public bool JustThisTile;
            public int GapLength;
            public TrackTileReference? StartTile;
            public TrackTileReference? EndTile;
        }
    }
}

internal static class TrackColorVisualResolver
{
    private const uint TrackColorFlag = 0x80u;

    internal static uint[] ResolveFlags(LevelDocument level)
    {
        int floorCount = level.FloorCount;
        var output = new uint[floorCount];
        if (floorCount == 0)
            return output;

        TrackColorSourceData source = TrackColorMetadataCache.Get(level);
        List<TrackColorSourceEvent> liveEvents = BuildLiveEvents(level, source);
        TrackColorStyle[] styles = new TrackColorStyle[floorCount];
        TrackColorStyle persistent = source.InitialStyle;

        var colorEvents = liveEvents
            .Where(static item => item.Active && item.EventType == "ColorTrack")
            .GroupBy(static item => item.Floor)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        for (int floor = 0; floor < floorCount; floor++)
        {
            styles[floor] = persistent;
            if (!colorEvents.TryGetValue(floor, out TrackColorSourceEvent[]? events))
                continue;

            foreach (TrackColorSourceEvent item in events)
            {
                TrackColorStyle next = ApplyStyle(persistent, item);
                styles[floor] = next;
                if (!item.JustThisTile)
                    persistent = next;
            }
        }

        foreach (TrackColorSourceEvent item in liveEvents.Where(static item => item.Active && item.EventType == "RecolorTrack"))
        {
            int start = ResolveReference(item.StartTile, item.Floor, floorCount);
            int end = ResolveReference(item.EndTile, item.Floor, floorCount);
            if (end < start)
                (start, end) = (end, start);

            start = Math.Clamp(start, 0, floorCount - 1);
            end = Math.Clamp(end, 0, floorCount - 1);
            int stride = Math.Max(1, item.GapLength + 1);
            TrackColorStyle recolor = ApplyStyle(source.InitialStyle, item);
            for (int floor = start; floor <= end; floor += stride)
                styles[floor] = recolor;
        }

        for (int floor = 0; floor < floorCount; floor++)
            output[floor] = PackStyle(styles[floor], floor);
        return output;
    }

    private static List<TrackColorSourceEvent> BuildLiveEvents(LevelDocument level, TrackColorSourceData source)
    {
        var bySource = source.Events.ToDictionary(static item => item.SourceIndex);
        var fallback = source.Events
            .GroupBy(static item => (item.Floor, item.EventType))
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<TrackColorSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var usedSources = new HashSet<int>();
        var result = new List<TrackColorSourceEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (action.EventType is not ("ColorTrack" or "RecolorTrack"))
                continue;

            TrackColorSourceEvent? sourceEvent = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out TrackColorSourceEvent? indexed))
            {
                sourceEvent = indexed;
                usedSources.Add(indexed.SourceIndex);
            }
            else if (fallback.TryGetValue((action.Floor, action.EventType), out Queue<TrackColorSourceEvent>? queue))
            {
                while (queue.Count > 0 && usedSources.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    sourceEvent = queue.Dequeue();
                    usedSources.Add(sourceEvent.SourceIndex);
                }
            }

            TrackColorSourceEvent live = sourceEvent is null
                ? new TrackColorSourceEvent(
                    action.SourceIndex,
                    action.Floor,
                    action.EventType,
                    action.Active,
                    null,
                    null,
                    null,
                    null,
                    false,
                    0,
                    null,
                    null)
                : sourceEvent with
                {
                    SourceIndex = action.SourceIndex,
                    Floor = action.Floor,
                    Active = action.Active
                };

            if (action.PropertyOverrides is JsonObject overrides)
                live = Overlay(live, overrides, action);
            result.Add(live);
        }

        result.Sort(static (a, b) =>
        {
            int floor = a.Floor.CompareTo(b.Floor);
            if (floor != 0) return floor;
            return a.SourceIndex.CompareTo(b.SourceIndex);
        });
        return result;
    }

    private static TrackColorSourceEvent Overlay(TrackColorSourceEvent source, JsonObject obj, LevelAction action)
    {
        return source with
        {
            Floor = action.Floor,
            Active = action.Active,
            ColorType = GetString(obj, "trackColorType") ?? source.ColorType,
            PrimaryColor = GetString(obj, "trackColor") ?? source.PrimaryColor,
            SecondaryColor = GetString(obj, "secondaryTrackColor") ?? source.SecondaryColor,
            PulseLength = GetInt(obj, "trackPulseLength") ?? source.PulseLength,
            JustThisTile = GetBool(obj, "justThisTile") ?? source.JustThisTile,
            GapLength = Math.Max(0, GetInt(obj, "gapLength") ?? source.GapLength),
            StartTile = GetReference(obj["startTile"]) ?? source.StartTile,
            EndTile = GetReference(obj["endTile"]) ?? source.EndTile
        };
    }

    private static TrackColorStyle ApplyStyle(TrackColorStyle fallback, TrackColorSourceEvent item) => new(
        item.ColorType ?? fallback.ColorType,
        item.PrimaryColor ?? fallback.PrimaryColor,
        item.SecondaryColor ?? fallback.SecondaryColor,
        Math.Max(1, item.PulseLength ?? fallback.PulseLength));

    private static int ResolveReference(TrackTileReference? reference, int eventFloor, int floorCount)
    {
        if (reference is null)
            return eventFloor;

        return reference.Mode switch
        {
            "Start" => reference.Offset,
            "End" => floorCount - 1 + reference.Offset,
            _ => eventFloor + reference.Offset
        };
    }

    private static uint PackStyle(TrackColorStyle style, int floor)
    {
        (byte r, byte g, byte b) = ResolveColor(style, floor);
        return TrackColorFlag |
               ((uint)r << 8) |
               ((uint)g << 16) |
               ((uint)b << 24);
    }

    private static (byte R, byte G, byte B) ResolveColor(TrackColorStyle style, int floor)
    {
        if (string.Equals(style.ColorType, "Rainbow", StringComparison.OrdinalIgnoreCase))
            return Hsv((floor * 0.055) % 1.0, 0.72, 1.0);

        bool secondary = string.Equals(style.ColorType, "Stripes", StringComparison.OrdinalIgnoreCase) &&
                         ((floor / Math.Max(1, style.PulseLength)) & 1) != 0;
        string text = secondary ? style.SecondaryColor : style.PrimaryColor;
        return ParseColor(text, (0xDE, 0xBB, 0x7B));
    }

    private static (byte R, byte G, byte B) ParseColor(string? text, (byte R, byte G, byte B) fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string value = text.Trim().TrimStart('#');
        if (value.Length < 6 ||
            !byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return fallback;
        return (r, g, b);
    }

    private static (byte R, byte G, byte B) Hsv(double h, double s, double v)
    {
        h = ((h % 1.0) + 1.0) % 1.0;
        double sector = h * 6.0;
        int i = (int)Math.Floor(sector);
        double f = sector - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - s * f);
        double t = v * (1.0 - s * (1.0 - f));
        (double r, double g, double b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static int? GetInt(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out int result))
            return result;
        return value.TryGetValue(out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static bool? GetBool(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out bool result) ? result : null;

    private static TrackTileReference? GetReference(JsonNode? node)
    {
        if (node is not JsonArray { Count: >= 2 } pair)
            return null;
        int? offset = pair[0] is JsonValue first && first.TryGetValue(out int parsed) ? parsed : null;
        string? mode = pair[1] is JsonValue second && second.TryGetValue(out string? text) ? text : null;
        return offset is int value ? new TrackTileReference(value, mode ?? "ThisTile") : null;
    }
}
